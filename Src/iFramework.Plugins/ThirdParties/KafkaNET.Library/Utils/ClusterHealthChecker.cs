﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using IFramework.Infrastructure.Logging;
using IFramework.IoC;
using Kafka.Client.Cfg;
using Kafka.Client.Cluster;
using Kafka.Client.Exceptions;
using Kafka.Client.Requests;
using Kafka.Client.ZooKeeperIntegration;
using ZooKeeperNet;

namespace Kafka.Client.Utils
{
    /// <summary>
    ///     Helper class to inspect Kafka and Zookeper cluster health (eg. Topics partitions, brokers availability).
    /// </summary>
    public class ClusterHealthChecker : KafkaClientBase
    {
        public static ILogger Logger = IoCFactory.Resolve<ILoggerFactory>().Create(typeof(ClusterHealthChecker));

        private readonly ConsumerConfiguration config;
        private readonly object shuttingDownLock = new object();
        private volatile bool disposed;
        private Cluster.Cluster kafkaCluster;
        private IZooKeeperClient zkClient;

        public ClusterHealthChecker(ConsumerConfiguration config)
        {
            this.config = config;
            ConnectZk();
        }

        /// <summary>
        ///     Get state of all partitions for specified topics. If topics not specified will automatically retrieve all topics
        ///     within a cluster
        /// </summary>
        /// <param name="topics">
        ///     Topics name to inspect. If topics are null <see cref="Kafka.Client.Utils.ClusterHealthChecker" />
        ///     will automatically retrieve all topics from ZooKeeper servers
        /// </param>
        /// <returns>Dictionary where Key is topic name and Value is another Dictionary of partitions states for that topic.</returns>
        /// <remarks>
        ///     If topics are null <see cref="Kafka.Client.Utils.ClusterHealthChecker" /> will automatically retrieve all
        ///     topics within a cluster
        /// </remarks>
        public IDictionary<string, Dictionary<int, TopicPartitionState>> GetZooKeeperTopicsPartitionsState(
            ICollection<string> topics = null)
        {
            // retrive latest brokers info
            RefreshKafkaBrokersInfo();

            if (topics == null)
            {
                IEnumerable<string> zkTopics;
                try
                {
                    zkTopics = zkClient.GetChildren(ZkUtils.BrokerTopicsPath);
                }
                catch (KeeperException e)
                {
                    if (e.ErrorCode == KeeperException.Code.NONODE)
                    {
                        zkTopics = null;
                    }
                    else
                    {
                        throw;
                    }
                }

                if (zkTopics == null)
                {
                    throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                                                              "Can't automatically retrieve topic list from ZooKeeper state"));
                }

                topics = zkTopics.ToList();
            }

            Logger.InfoFormat("Collecting topics metadata from ZooKeeper state for topics {0}",
                              string.Join(",", topics));

            var topicMetadataMap = new Dictionary<string, Dictionary<int, TopicPartitionState>>();

            foreach (var topic in topics)
            {
                try
                {
                    Logger.DebugFormat("Collecting topic information from ZooKeeper state for topic {0}", topic);
                    topicMetadataMap[topic] = ProcessTopic(topic);
                }
                catch (NoPartitionsForTopicException exc)
                {
                    Logger.ErrorFormat("Could not find any partitions for topic {0}. Error: {1}", exc.Topic,
                                       exc.FormatException());
                    topicMetadataMap[topic] = new Dictionary<int, TopicPartitionState>();
                }
                catch (Exception exc)
                {
                    Logger.ErrorFormat(
                                       "Unexpected error when trying to collect topic '{0}' metadata from ZooKeeper state. Error: {1}",
                                       topic, exc.FormatException());
                    throw;
                }
            }

            Logger.InfoFormat("Completed collecting topics metadata from ZooKeeper state for topics {0}",
                              string.Join(",", topics));

            return topicMetadataMap;
        }


        /// <summary>
        ///     Check that brokers alive by sending topic metadata request to them
        /// </summary>
        /// <param name="brokers">Collection of brokers to check. If null - brokers list will be retrieved from ZooKeeper state</param>
        /// <returns>
        ///     Dictionary where Key is Broker Id and Value indicates whether Broker responds to requests.
        ///     Value is true when Broker TopicMetadataRequest was successfully sent to broker and any response was recieved back.
        ///     Value is false when connection to Broker failed or
        /// </returns>
        /// <remarks>
        ///     If brokers not specified this method will only ping brokers that exist in ZooKeeper state
        /// </remarks>
        public IDictionary<int, bool> GetKafkaBrokersAliveState(ICollection<Broker> brokers = null)
        {
            // retrive latest brokers info
            if (brokers == null)
            {
                RefreshKafkaBrokersInfo();
            }

            brokers = brokers ?? kafkaCluster.Brokers.Values;
            var brokersConnectionString = string.Join(", ",
                                                      kafkaCluster.Brokers.Values.Select(x => string.Join(":", x.Id, x.Host, x.Port)));
            Logger.InfoFormat("Collecting brokers alive state for brokers {0}", brokersConnectionString);

            var brokerAliveStateMap = new SortedDictionary<int, bool>();
            foreach (var broker in brokers)
            {
                try
                {
                    Logger.DebugFormat("Sending request to broker #{0} {1}:{2}", broker.Id, broker.Host, broker.Port);
                    using (var kafkaConnection = new KafkaConnection(broker.Host, broker.Port, config.BufferSize,
                                                                     config.SendTimeout, config.ReceiveTimeout, int.MaxValue))
                    {
                        // send topic offset request for random non-existing topic
                        var requestInfos = new Dictionary<string, List<PartitionOffsetRequestInfo>>();
                        requestInfos[Guid.NewGuid().ToString("N")] = new List<PartitionOffsetRequestInfo>
                        {
                            new PartitionOffsetRequestInfo(0, OffsetRequest.EarliestTime, 1)
                        };
                        kafkaConnection.Send(new OffsetRequest(requestInfos));
                    }

                    brokerAliveStateMap[broker.Id] = true;
                }
                catch (Exception exc)
                {
                    Logger.WarnFormat("Failed to send request to broker #{0} {1}:{2}. Error:", broker.Id, broker.Host,
                                      broker.Port, exc.FormatException());
                    brokerAliveStateMap[broker.Id] = false;
                }
            }

            Logger.InfoFormat("Completed collecting brokers alive state for brokers {0}", brokersConnectionString);

            return brokerAliveStateMap;
        }

        /// <summary>
        ///     Check that ZooKeeper VMs alive by sending telnet command to each server
        /// </summary>
        /// <returns>
        ///     Dictionary where Key is ZooKeeper host name and Value dictionary of IPAddress and ping result.
        ///     Ping result value is true when successfully connected to ZooKeeper VM and it successfully responded to TELNET
        ///     command "ruok".
        ///     Otherwise Value is false.
        /// </returns>
        public IDictionary<string, IDictionary<string, bool>> GetZooKeeperHostsAliveState()
        {
            List<Exception> errList;
            return GetZooKeeperHostsAliveState(out errList);
        }

        /// <summary>
        ///     Check that ZooKeeper VMs alive by sending telnet command to each server
        /// </summary>
        /// <param name="errorList">The list of errors occured while check was performed</param>
        /// <returns>
        ///     Dictionary where Key is ZooKeeper host name and Value dictionary of IPAddress and ping result.
        ///     Ping result value is true when successfully connected to ZooKeeper VM and it successfully responded to TELNET
        ///     command "ruok".
        ///     Otherwise Value is false.
        /// </returns>
        public IDictionary<string, IDictionary<string, bool>> GetZooKeeperHostsAliveState(out List<Exception> errorList)
        {
            errorList = new List<Exception>();
            var hostList = GetHosts(config.ZooKeeper.ZkConnect);

            Logger.InfoFormat("Collecting ZooKeeper VMs alive state by connecting over TCP to machines {0}",
                              config.ZooKeeper.ZkConnect);

            var zkaliveStateMap = new Dictionary<string, IDictionary<string, bool>>();
            foreach (var host in hostList)
            {
                zkaliveStateMap[host.Item1] = new Dictionary<string, bool>();
                foreach (var hostIpAddress in host.Item2)
                {
                    var hostIpStr = hostIpAddress.ToString();
                    try
                    {
                        Logger.DebugFormat("Sending request to ZooKeeper VM host '{0}' IP '{1}'", host.Item1,
                                           hostIpStr);
                        zkaliveStateMap[host.Item1][hostIpStr] = PingZooKeeperHost(hostIpAddress);
                    }
                    catch (Exception exc)
                    {
                        Logger.WarnFormat("Failed Sending request to ZooKeeper VM host '{0}' IP '{1}'. Error: ",
                                          host.Item1, hostIpStr, exc.FormatException());
                        zkaliveStateMap[host.Item1][hostIpStr] = false;
                        errorList.Add(exc);
                    }
                }
            }

            Logger.InfoFormat("Completed Collecting ZooKeeper VMs alive state by connecting over TCP to machines {0}",
                              config.ZooKeeper.ZkConnect);

            return zkaliveStateMap;
        }

        /// <summary>
        ///     Send "ruok" command (ping command analog) to ZooKeeper server with specified Ip Address and port
        /// </summary>
        /// <param name="addr">The IP Address and port</param>
        /// <returns>
        ///     True if was able to connect to server, send "ruok" command and recieve "imok" back. Otherwise throws exception
        ///     with error description
        /// </returns>
        private static bool PingZooKeeperHost(IPEndPoint addr)
        {
            const int DefaultReadTimeOut = 500;
            const int DefaultSendTimeOut = 500;

            using (var tcpClient = new TcpClient())
            {
                try
                {
                    // 1. Connect
                    tcpClient.LingerState = new LingerOption(false, 0);
                    tcpClient.NoDelay = true;
                    tcpClient.ReceiveTimeout = DefaultReadTimeOut;
                    tcpClient.SendTimeout = DefaultSendTimeOut;
                    tcpClient.Connect(addr.Address, addr.Port);

                    // 2. Send command
                    const string PingTelNetCommand = "ruok";
                    const string PingTelNetResponse = "imok";
                    var commandBytes = Encoding.ASCII.GetBytes(PingTelNetCommand);
                    tcpClient.GetStream().Write(commandBytes, 0, commandBytes.Length);

                    // 3. Read response
                    var readBuffer = new byte[100];
                    var bytesRead = -1;
                    var gotOkResponse = false;
                    while (bytesRead != 0)
                    {
                        bytesRead = tcpClient.GetStream().Read(readBuffer, 0, readBuffer.Length);
                        if (bytesRead != 0)
                        {
                            var response = Encoding.ASCII.GetString(readBuffer, 0, bytesRead);
                            Logger.InfoFormat(
                                              "Recieved a response to ruok command from ZooKeeper host {0}. Response: {1}",
                                              addr.ToString(), response);
                            if (response.Contains(PingTelNetResponse))
                            {
                                gotOkResponse = true;
                            }
                        }
                    }

                    if (!gotOkResponse)
                    {
                        throw new Exception("No 'imok' recieved right after connected to ZooKeeper host");
                    }
                }
                finally
                {
                    try
                    {
                        tcpClient.GetStream().Close();
                    }
                    catch (Exception) { }
                }
            }

            return true;
        }

        private void RefreshKafkaBrokersInfo()
        {
            kafkaCluster = new Cluster.Cluster(zkClient);
        }

        private Dictionary<int, TopicPartitionState> ProcessTopic(string topic)
        {
            // might throw NoPartitionsForTopicException
            var topicPartitionMap = ZkUtils.GetPartitionsForTopics(zkClient, new[] {topic});

            var partitionsMetadata = new Dictionary<int, TopicPartitionState>();
            foreach (var partitionId in topicPartitionMap[topic])
            {
                var partitionIdInt = int.Parse(partitionId);
                partitionsMetadata[partitionIdInt] = ZkUtils.GetPartitionState(zkClient, topic, partitionIdInt);
            }

            return partitionsMetadata;
        }

        private void ConnectZk()
        {
            Logger.InfoFormat("Connecting to zookeeper instance at {0}", config.ZooKeeper.ZkConnect);
            zkClient = new ZooKeeperClient(config.ZooKeeper.ZkConnect, config.ZooKeeper.ZkSessionTimeoutMs,
                                           ZooKeeperStringSerializer.Serializer, config.ZooKeeper.ZkConnectionTimeoutMs);
            zkClient.Connect();
        }

        private static List<Tuple<string, IList<IPEndPoint>>> GetHosts(string hostLst)
        {
            var strArray = hostLst.Split(',');
            var list = new List<Tuple<string, IList<IPEndPoint>>>();
            foreach (var str in strArray)
            {
                var host = str;
                var port = 2181;
                var length = str.LastIndexOf(':');
                if (length >= 0)
                {
                    if (length < str.Length - 1)
                    {
                        port = int.Parse(str.Substring(length + 1));
                    }
                    host = str.Substring(0, length);
                }

                list.Add(new Tuple<string, IList<IPEndPoint>>(host,
                                                              ResolveHostToIpAddresses(host).Select(address => new IPEndPoint(address, port)).ToList()));
            }

            return list;
        }

        private static IEnumerable<IPAddress> ResolveHostToIpAddresses(string host)
        {
            return Dns.GetHostAddresses(host).Where(x => x.AddressFamily == AddressFamily.InterNetwork);
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            if (disposed)
            {
                return;
            }

            Logger.Info("ClusterHealthChecker shutting down");

            try
            {
                zkClient.UnsubscribeAll();

                Thread.Sleep(1000);

                lock (shuttingDownLock)
                {
                    if (disposed)
                    {
                        return;
                    }

                    disposed = true;
                }

                if (zkClient != null)
                {
                    zkClient.Dispose();
                }
            }
            catch (Exception exc)
            {
                Logger.Debug("Ignoring unexpected errors on shutting down", exc);
            }

            Logger.Info("ClusterHealthChecker shut down completed");
        }
    }
}