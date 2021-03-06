﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Controllers;
using System.Web.Http.ModelBinding;
using IFramework.Exceptions;
using IFramework.Infrastructure;

namespace IFramework.AspNet
{
    public class ApiControllerBase : ApiController
    {
        protected string GetModelErrorMessage(ModelStateDictionary modelState)
        {
            return string.Join(";", modelState.Where(m => (m.Value?.Errors?.Count ?? 0) > 0)
                                              .Select( m => $"{m.Key}:{string.Join(",", m.Value.Errors.Select(e => e.ErrorMessage + e.Exception?.Message))}"));
        }

        #region process wrapping

        protected ApiResult<T> Process<T>(Func<T> func,
                                          bool needRetry = true,
                                          Func<Exception, string> getExceptionMessage = null,
                                          Func<ModelStateDictionary, string> getModelErrorMessage = null)
        {
            if (ModelState.IsValid)
            {
                var apiResult = ExceptionManager.Process(func, needRetry, getExceptionMessage: getExceptionMessage);
                return apiResult;
            }
            getModelErrorMessage = getModelErrorMessage ?? GetModelErrorMessage;
            return
                new ApiResult<T>
                    (
                     ErrorCode.InvalidParameters,
                     getModelErrorMessage(ModelState)
                    );
        }

        protected ApiResult Process(Action action,
                                    bool needRetry = true,
                                    Func<Exception, string> getExceptionMessage = null,
                                    Func<ModelStateDictionary, string> getModelErrorMessage = null)
        {
            if (ModelState.IsValid)
            {
                var apiResult = ExceptionManager.Process(action, needRetry, getExceptionMessage: getExceptionMessage);
                return apiResult;
            }
            getModelErrorMessage = getModelErrorMessage ?? GetModelErrorMessage;
            return
                new ApiResult
                    (
                     ErrorCode.InvalidParameters,
                     getModelErrorMessage(ModelState)
                    );
        }

        private List<CookieHeaderValue> _cookies;

        protected void AddCookies(params CookieHeaderValue[] cookies)
        {
            if (_cookies == null)
            {
                _cookies = new List<CookieHeaderValue>();
            }
            _cookies.AddRange(cookies);
        }

        protected string TryGetCookie(string key, string defaultValue)
        {
            try
            {
                var value = defaultValue;
                var cookie = Request.Headers.GetCookies(key).FirstOrDefault();
                if (cookie != null)
                {
                    value = cookie[key].Value;
                }
                return value;
            }
            catch (Exception)
            {
                return defaultValue;
            }
        }

        protected void RemoveCookies(string key)
        {
            var cookies = Request.Headers.GetCookies(key).ToList();
            cookies.ForEach(c => c.Expires = DateTimeOffset.Now.AddDays(-1));
            AddCookies(cookies.ToArray());
        }

        public override Task<HttpResponseMessage> ExecuteAsync(HttpControllerContext controllerContext,
                                                               CancellationToken cancellationToken)
        {
            return base.ExecuteAsync(controllerContext, cancellationToken)
                       .ContinueWith(t =>
                       {
                           if (t.IsFaulted)
                           {
                               var httpResponseException = t.Exception.GetBaseException() as HttpResponseException;
                               if (httpResponseException != null)
                               {
                                   return httpResponseException.Response;
                               }
                           }
                           if (_cookies != null && _cookies.Count > 0)
                           {
                               t.Result.Headers.AddCookies(_cookies);
                           }
                           return t.Result;
                       });
        }

        protected async Task<ApiResult> ProcessAsync(Func<Task> func,
                                                     bool continueOnCapturedContext = false,
                                                     bool needRetry = true,
                                                     Func<Exception, string> getExceptionMessage = null,
                                                     Func<ModelStateDictionary, string> getModelErrorMessage = null)
        {
            if (ModelState.IsValid)
            {
                return await ExceptionManager.ProcessAsync(func,
                                                           continueOnCapturedContext,
                                                           needRetry,
                                                           getExceptionMessage: getExceptionMessage)
                                             .ConfigureAwait(continueOnCapturedContext);
            }
            getModelErrorMessage = getModelErrorMessage ?? GetModelErrorMessage;
            return
                new ApiResult
                    (
                     ErrorCode.InvalidParameters,
                     getModelErrorMessage(ModelState)
                    );
        }

        protected async Task<ApiResult<T>> ProcessAsync<T>(Func<Task<T>> func,
                                                           bool continueOnCapturedContext = false,
                                                           bool needRetry = true,
                                                           Func<Exception, string> getExceptionMessage = null,
                                                           Func<ModelStateDictionary, string> getModelErrorMessage = null)
        {
            if (ModelState.IsValid)
            {
                return await ExceptionManager.ProcessAsync(func, continueOnCapturedContext, needRetry,
                                                           getExceptionMessage: getExceptionMessage)
                                             .ConfigureAwait(continueOnCapturedContext);
            }
            getModelErrorMessage = getModelErrorMessage ?? GetModelErrorMessage;
            return
                new ApiResult<T>
                    (
                     ErrorCode.InvalidParameters,
                     getModelErrorMessage(ModelState)
                    );
        }

        protected string GetClientIp(HttpRequestMessage request = null)
        {
            return (request ?? Request).GetClientIp();
        }

        #endregion
    }
}