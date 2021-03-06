using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Elastic.Apm.Tests.Mocks;
using Elastic.Apm;
using Elastic.Apm.Config;
using Elastic.Apm.DiagnosticListeners;
using Elastic.Apm.DiagnosticSource;
using Elastic.Apm.Logging;
using Elastic.Apm.Model.Payload;
using Xunit;

namespace Elastic.Apm.Tests
{
    public class HttpDiagnosticListenerTest
    {
        public HttpDiagnosticListenerTest() => TestHelper.ResetAgentAndEnvVars();

        /// <summary>
        /// Calls the OnError method on the HttpDiagnosticListener and makes sure that the correct error message is logged.
        /// </summary>
        [Fact]
        public void OnErrorLog()
        {
            Apm.Agent.SetLoggerType<TestLogger>();
            var listener = new HttpDiagnosticListener();

            var exceptionMessage = "Ooops, this went wrong";
            var fakeException = new Exception(exceptionMessage);
            listener.OnError(fakeException);

            Assert.Equal($"Error {listener.Name}: Exception in OnError, Exception-type:{nameof(Exception)}, Message:{exceptionMessage}", (listener.Logger as TestLogger)?.Lines?.FirstOrDefault());
        }

        /// <summary>
        /// Builds an HttpRequestMessage and calls HttpDiagnosticListener.OnNext directly with it.
        /// Makes sure that the processingRequests dictionary captures the ongoing transaction.
        /// </summary>
        [Fact]
        public void OnNextWithStart()
        {
            StartTransaction();
            var listener = new HttpDiagnosticListener();

            var request = new HttpRequestMessage(HttpMethod.Get, "https://elastic.co");

            //Simulate Start
            listener.OnNext(new KeyValuePair<string, object>("System.Net.Http.HttpRequestOut.Start", new { Request = request }));
            Assert.Single(listener.processingRequests);
            Assert.Equal(request.RequestUri.ToString(), listener.processingRequests[request].Context.Http.Url);
            Assert.Equal(HttpMethod.Get.ToString(), listener.processingRequests[request].Context.Http.Method);
        }

        /// <summary>
        /// Simulates the complete lifecycle of an HTTP request.
        /// It builds an HttpRequestMessage and an HttpResponseMessage
        /// and passes them to the OnNext method.
        /// Makes sure that a Span with an Http context is captured.
        /// </summary>
        [Fact]
        public void OnNextWithStartAndStop()
        {
            StartTransaction();
            var listener = new HttpDiagnosticListener();

            var request = new HttpRequestMessage(HttpMethod.Get, "https://elastic.co");
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);

            //Simulate Start
            listener.OnNext(new KeyValuePair<string, object>("System.Net.Http.HttpRequestOut.Start", new { Request = request }));
            //Simulate Stop
            listener.OnNext(new KeyValuePair<string, object>("System.Net.Http.HttpRequestOut.Stop", new { Request = request, Response = response }));
            Assert.Empty(listener.processingRequests);

            Assert.Equal(request.RequestUri.ToString(), TransactionContainer.Transactions.Value[0].Spans[0].Context.Http.Url);
            Assert.Equal(HttpMethod.Get.ToString(), TransactionContainer.Transactions.Value[0].Spans[0].Context.Http.Method);
        }

        /// <summary>
        /// Calls OnNext with System.Net.Http.HttpRequestOut.Stop twice.
        /// Makes sure that the transaction is only captured once and the span is also only captured once. 
        /// Also make sure that there is an error log.
        /// </summary>
        [Fact]
        public void OnNextWithStartAndStopTwice()
        {
            StartTransaction();
            Apm.Agent.SetLoggerType<TestLogger>();
            Apm.Agent.Config.LogLevel = LogLevel.Warning; //make sure we have high enough log level
            var listener = new HttpDiagnosticListener();

            var request = new HttpRequestMessage(HttpMethod.Get, "https://elastic.co");
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);

            //Simulate Start
            listener.OnNext(new KeyValuePair<string, object>("System.Net.Http.HttpRequestOut.Start", new { Request = request }));
            //Simulate Stop
            listener.OnNext(new KeyValuePair<string, object>("System.Net.Http.HttpRequestOut.Stop", new { Request = request, Response = response }));
            //Simulate Stop again. This should not happen, still we test for this.
            listener.OnNext(new KeyValuePair<string, object>("System.Net.Http.HttpRequestOut.Stop", new { Request = request, Response = response }));

            Console.Write("lines: " + (listener.Logger as TestLogger).Lines.Count);

            Assert.Equal($"{LogLevel.Warning} {listener.Name}: Failed capturing request '{HttpMethod.Get} {request.RequestUri}' in System.Net.Http.HttpRequestOut.Stop. This Span will be skipped in case it wasn't captured before.", 
                         (listener.Logger as TestLogger).Lines[0]);
            Assert.Single(TransactionContainer.Transactions.Value);
            Assert.Single(TransactionContainer.Transactions.Value[0].Spans);
        }

        /// <summary>
        /// Calls HttpDiagnosticListener.OnNext with types that are unknown.
        /// The test makes sure that in this case still no exception is thrown from the OnNext method.
        /// </summary>
        [Fact]
        public void UnknownObjectToOnNext()
        {           
            var listener = new HttpDiagnosticListener();
            var myFake = new StringBuilder(); //just a random type that is not planned to be passed into OnNext

            var exception = 
                Record.Exception(() =>
                {
                    listener.OnNext(new KeyValuePair<string, object>("System.Net.Http.HttpRequestOut.Start", myFake));
                });

            Assert.Null(exception);
        }

        /// <summary>
        /// Passes null instead of a valid HttpRequestMessage into HttpDiagnosticListener.OnNext
        /// and makes sure that still no exception is thrown.
        /// </summary>
        [Fact]
        public void NullValueToOnNext()
        {
            var listener = new HttpDiagnosticListener();

            var exception =
                Record.Exception(() =>
                {
                    listener.OnNext(new KeyValuePair<string, object>("System.Net.Http.HttpRequestOut.Start", null));
                });

            Assert.Null(exception);
        }

        /// <summary>
        /// Passes a null key with null value instead of a valid HttpRequestMessage into HttpDiagnosticListener.OnNext
        /// and makes sure that still no exception is thrown.
        /// </summary>
        [Fact]
        public void NullKeyValueToOnNext()
        {
            var listener = new HttpDiagnosticListener();

            var exception =
                Record.Exception(() =>
                {
                    listener.OnNext(new KeyValuePair<string, object>(null, null));
                });

            Assert.Null(exception);
        }

        /// <summary>
        /// Sends a simple real HTTP GET message and makes sure that 
        /// HttpDiagnosticListener captures it.
        /// </summary>
        [Fact]
        public async Task TestSimpleOutgoingHttpRequest()
        {
            RegisterListenerAndStartTransaction();

            using (LocalServer localServer = new LocalServer())
            {
                var httpClient = new HttpClient();
                var res = await httpClient.GetAsync(localServer.Uri);

                Assert.True(res.IsSuccessStatusCode);
                Assert.Equal(localServer.Uri, TransactionContainer.Transactions.Value[0].Spans[0].Context.Http.Url);
            }

            Assert.Equal(200, TransactionContainer.Transactions.Value[0].Spans[0].Context.Http.Status_code);
            Assert.Equal(HttpMethod.Get.ToString(), TransactionContainer.Transactions.Value[0].Spans[0].Context.Http.Method);
        }

        /// <summary>
        /// Sends a simple real HTTP POST message and the server responsds with 500
        /// The test makes sure HttpDiagnosticListener captures the POST method and
        /// the response code correctly
        /// </summary>
        [Fact]
        public async Task TestNotSuccesfulOutgoingHttpPostRequest()
        {
            RegisterListenerAndStartTransaction();

            using (LocalServer localServer = new LocalServer(ctx =>
            {
                ctx.Response.StatusCode = 500;
            }))
            {
                var httpClient = new HttpClient();
                var res = await httpClient.PostAsync(localServer.Uri, new StringContent("foo"));

                Assert.False(res.IsSuccessStatusCode);
                Assert.Equal(localServer.Uri, TransactionContainer.Transactions.Value[0].Spans[0].Context.Http.Url);
            }

            Assert.Equal(500, TransactionContainer.Transactions.Value[0].Spans[0].Context.Http.Status_code);
            Assert.Equal(HttpMethod.Post.ToString(), TransactionContainer.Transactions.Value[0].Spans[0].Context.Http.Method);
        }

        private void RegisterListenerAndStartTransaction()
        {
            new ElasticCoreListeners().Start();
            StartTransaction();
        }


        private void StartTransaction() 
        => TransactionContainer.Transactions.Value = new List<Transaction>()
        {
                new Transaction()
                {
                    Name = $"{nameof(TestSimpleOutgoingHttpRequest)}",
                    Id = Guid.NewGuid(),
                    Type = "request",
                    TimestampInDateTime = DateTime.UtcNow,
                }
        };
    }
}
