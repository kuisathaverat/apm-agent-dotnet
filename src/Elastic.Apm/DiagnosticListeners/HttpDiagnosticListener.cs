﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Elastic.Apm.Config;
using Elastic.Apm.DiagnosticSource;
using Elastic.Apm.Logging;
using Elastic.Apm.Model.Payload;

namespace Elastic.Apm.DiagnosticListeners
{
    /// <summary>
    /// Captures web requests initiated by <see cref="System.Net.Http.HttpClient"/>
    /// </summary>
    public class HttpDiagnosticListener : IDiagnosticListener
    {
        public string Name => "HttpHandlerDiagnosticListener";

        /// <summary>
        /// Keeps track of ongoing requests
        /// </summary>
        internal readonly ConcurrentDictionary<HttpRequestMessage, Span> processingRequests = new ConcurrentDictionary<HttpRequestMessage, Span>();
        private readonly IConfig agentConfig;

        private readonly AbstractLogger logger;
        internal AbstractLogger Logger => logger;

        public HttpDiagnosticListener()
        {
            agentConfig = Apm.Agent.Config;
            logger = Apm.Agent.CreateLogger(Name);
        }

        public void OnCompleted() { }

        public void OnError(Exception error)
            => logger.LogError($"Exception in OnError, Exception-type:{error.GetType().Name}, Message:{error.Message}");

        public void OnNext(KeyValuePair<string, object> kv)
        {
            if (kv.Value == null || String.IsNullOrEmpty(kv.Key))
            {
                return;
            }

            if (!(kv.Value.GetType().GetTypeInfo().GetDeclaredProperty("Request")?.GetValue(kv.Value) is HttpRequestMessage request))
            {
                return;
            }

            if (IsRequestFiltered(request?.RequestUri))
            {
                return;
            }

            switch (kv.Key)
            {
                case "System.Net.Http.HttpRequestOut.Start": //TODO: look for consts
                    if (TransactionContainer.Transactions == null || TransactionContainer.Transactions.Value == null)
                    {
                        return;
                    }

                    var transactionStartTime = TransactionContainer.Transactions.Value[0].TimestampInDateTime;
                    var utcNow = DateTime.UtcNow;

                    var http = new Http
                    {
                        Url = request?.RequestUri?.ToString(),
                        Method = request?.Method?.Method,
                    };

                    var span = new Span
                    {
                        Start = (decimal)(utcNow - transactionStartTime).TotalMilliseconds,
                        Name = $"{request?.Method} {request?.RequestUri?.ToString()}",
                        Type = "Http",
                        Context = new Span.ContextC
                        {
                            Http = http
                        }
                    };

                    if (processingRequests.TryAdd(request, span))
                    {
                        var frames = new System.Diagnostics.StackTrace().GetFrames();
                        var stackFrames = new List<Stacktrace>(frames.Length);

                        try
                        {
                            foreach (var item in frames)
                            {
                                var fileName = item?.GetMethod()?.DeclaringType?.Assembly?.GetName()?.Name;
                                if (String.IsNullOrEmpty(fileName))
                                {
                                    continue; //since filename is required by the server, if we don't have it we skip the frame
                                }

                                stackFrames.Add(new Stacktrace
                                {
                                    Function = item?.GetMethod()?.Name,
                                    Filename = fileName,
                                    Module = item?.GetMethod()?.ReflectedType?.Name
                                });
                            }
                        }
                        catch (Exception e)
                        {
                            logger.LogWarning($"Failed capturing stacktrace for {span.Name}");
                            logger.LogDebug($"{e.GetType().Name}: {e.Message}");
                        }

                        span.Stacktrace = stackFrames;
                    }
                    break;

                case "System.Net.Http.HttpRequestOut.Stop":
                    var response = kv.Value.GetType().GetTypeInfo().GetDeclaredProperty("Response").GetValue(kv.Value) as HttpResponseMessage;
                    var requestTaskStatusObj = kv.Value.GetType().GetTypeInfo().GetDeclaredProperty("RequestTaskStatus")?.GetValue(kv.Value);
                    if (requestTaskStatusObj != null)
                    {
                        var requestTaskStatus = (TaskStatus)requestTaskStatusObj;
                    }

                    if (processingRequests.TryRemove(request, out Span mspan))
                    {
                        //TODO: response can be null if for example the request Task is Faulted. 
                        //E.g. writing this from an airplane without internet, and requestTaskStatus is "Faulted" and response is null
                        //How do we report this? There is no response code in that case.
                        if (response != null)
                        {
                            mspan.Context.Http.Status_code = (int)response.StatusCode;
                        }

                        //TODO: there are better ways
                        var endTime = (DateTime.UtcNow - TransactionContainer.Transactions.Value[0].TimestampInDateTime).TotalMilliseconds;
                        mspan.Duration = endTime - (double)mspan.Start;

                        TransactionContainer.Transactions?.Value[0]?.Spans?.Add(mspan);
                    }
                    else
                    {
                        logger.LogWarning($"Failed capturing request"
                            + (!String.IsNullOrEmpty(request?.RequestUri?.AbsoluteUri) && !String.IsNullOrEmpty(request?.Method?.ToString()) ? $" '{request?.Method.ToString()} " : " ")
                            + (String.IsNullOrEmpty(request?.RequestUri?.AbsoluteUri) ? "" : $"{request?.RequestUri.AbsoluteUri}' ")
                            + "in System.Net.Http.HttpRequestOut.Stop. This Span will be skipped in case it wasn't captured before.");
                    }
                    break;
            }
        }

        /// <summary>
        /// Tells if the given request should be filtered from being captured. 
        /// </summary>
        /// <returns><c>true</c>, if request should not be captured, <c>false</c> otherwise.</returns>
        /// <param name="requestUri">Request URI. Can be null, which is not filtered</param>
        private bool IsRequestFiltered(Uri requestUri)
        {
            switch (requestUri)
            {
                case Uri uri when uri == null:
                    return true;
                case Uri uri when Apm.Agent.Config.ServerUrls.Any(n => n.IsBaseOf(uri)): //TODO: measure the perf of this!
                    return true;
                default:
                    return false;
            }
        }
    }
}