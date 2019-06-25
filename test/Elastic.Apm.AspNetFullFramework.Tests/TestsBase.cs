using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AspNetFullFrameworkSampleApp;
using AspNetFullFrameworkSampleApp.Controllers;
using Elastic.Apm.Api;
using Elastic.Apm.Config;
using Elastic.Apm.Helpers;
using Elastic.Apm.Logging;
using Elastic.Apm.Model;
using Elastic.Apm.Tests.MockApmServer;
using Elastic.Apm.Tests.TestHelpers;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Elastic.Apm.AspNetFullFramework.Tests
{
	public class TestsBase : IAsyncLifetime
	{
		private static readonly string TearDownPersistentDataReason;

		private static readonly bool TearDownPersistentData =
			EnvVarUtils.GetBoolValue("ELASTIC_APM_TESTS_FULL_FRAMEWORK_TEAR_DOWN_PERSISTENT_DATA", /* defaultValue: */ true,
				out TearDownPersistentDataReason);


		protected readonly AgentConfiguration AgentConfig = new AgentConfiguration();
		protected readonly bool SampleAppShouldHaveAccessToPerfCounters;
		private readonly Dictionary<string, string> _envVarsToSetForSampleAppPool;
		private readonly IisAdministration _iisAdministration;

		private readonly ITestOutputHelper _xUnitOutputHelper;
		private readonly IApmLogger _logger;
		private readonly MockApmServer _mockApmServer;
		private readonly int _mockApmServerPort;
		private readonly bool _sampleAppLogEnabled;
		private readonly string _sampleAppLogFilePath;
		private readonly bool _startMockApmServer;
		private readonly DateTimeOffset _testStartTime = DateTimeOffset.UtcNow;

		protected TestsBase(ITestOutputHelper xUnitOutputHelper,
			bool startMockApmServer = true,
			Dictionary<string, string> envVarsToSetForSampleAppPool = null,
			bool sampleAppShouldHaveAccessToPerfCounters = false,
			bool sampleAppLogEnabled = true
		)
		{
			_xUnitOutputHelper = xUnitOutputHelper;
			_logger = new ToAllSinksLogger(_xUnitOutputHelper).Scoped(nameof(TestsBase));

			_logger.Info()?.Log("Starting test: {FullUnitTestName}", GetCurrentTestDisplayName(_xUnitOutputHelper));

			_mockApmServer = new MockApmServer(_logger, GetCurrentTestDisplayName(_xUnitOutputHelper));
			_iisAdministration = new IisAdministration(_logger);
			_startMockApmServer = startMockApmServer;
			SampleAppShouldHaveAccessToPerfCounters = sampleAppShouldHaveAccessToPerfCounters;

			_mockApmServerPort = _startMockApmServer ? _mockApmServer.FindAvailablePortToListen() : ConfigConsts.DefaultValues.ApmServerPort;

			_sampleAppLogEnabled = sampleAppLogEnabled;
			_sampleAppLogFilePath = GetSampleAppLogFilePath();

			_envVarsToSetForSampleAppPool = envVarsToSetForSampleAppPool == null
				? new Dictionary<string, string>()
				: new Dictionary<string, string>(envVarsToSetForSampleAppPool);
			_envVarsToSetForSampleAppPool.TryAdd(ConfigConsts.EnvVarNames.ServerUrls, BuildApmServerUrl(_mockApmServerPort));

			if (_sampleAppLogEnabled) _envVarsToSetForSampleAppPool.TryAdd(LoggingConfig.LogFileEnvVarName, _sampleAppLogFilePath);
		}

		private static class DataSentByAgentVerificationConsts
		{
			internal const int LogMessageAfterNInitialAttempts = 30; // i.e., log the first message after 3 seconds (if it's still failing)
			internal const int LogMessageEveryNAttempts = 10; // i.e., log message every second (if it's still failing)
			internal const int MaxNumberOfAttemptsToVerify = 100;
			internal const int WaitBetweenVerifyAttemptsMs = 100;
		}

		internal static class SampleAppUrlPaths
		{
			internal static readonly SampleAppUrlPathData AboutPage =
				new SampleAppUrlPathData(HomeController.AboutPageRelativePath, 200);

			/// Contact page processing does HTTP Get for About page (additional transaction) and https://elastic.co/ - so 2 spans
			internal static readonly SampleAppUrlPathData ContactPage =
				new SampleAppUrlPathData(HomeController.ContactPageRelativePath, 200, /* transactionsCount: */ 2, /* spansCount: */ 2);

			internal static readonly SampleAppUrlPathData CustomSpanThrowsExceptionPage =
				new SampleAppUrlPathData(HomeController.CustomSpanThrowsPageRelativePath, 500, spansCount: 1, errorsCount: 1);

			internal static readonly SampleAppUrlPathData HomePage =
				new SampleAppUrlPathData(HomeController.HomePageRelativePath, 200);

			internal static readonly List<SampleAppUrlPathData> AllPaths = new List<SampleAppUrlPathData>()
			{
				new SampleAppUrlPathData("", 200),
				HomePage,
				ContactPage,
				CustomSpanThrowsExceptionPage,
				new SampleAppUrlPathData("Dummy_nonexistent_path", 404),
			};

			/// `CallReturnBadRequest' page processing does HTTP Get for `ReturnBadRequest' page (additional transaction) - so 1 span
			internal static readonly SampleAppUrlPathData CallReturnBadRequestPage =
				new SampleAppUrlPathData(HomeController.CallReturnBadRequestPageRelativePath,
					HomeController.DummyHttpStatusCode, /* transactionsCount: */ 2, /* spansCount: */ 1);


			internal static readonly SampleAppUrlPathData GetDotNetRuntimeDescriptionPage =
				new SampleAppUrlPathData(HomeController.GetDotNetRuntimeDescriptionPageRelativePath, 200);


			internal static readonly SampleAppUrlPathData ReturnBadRequestPage =
				new SampleAppUrlPathData(HomeController.ReturnBadRequestPageRelativePath, (int)HttpStatusCode.BadRequest);

			/// errorsCount for ThrowsNameCouldNotBeResolvedPage is 0 because we don't automatically capture exceptions
			/// that escaped from Full Framework ASP.NET transactions as errors (yet)
			internal static readonly SampleAppUrlPathData ThrowsInvalidOperationPage =
				new SampleAppUrlPathData(HomeController.ThrowsInvalidOperationPageRelativePath, 500);
		}

		protected IApmLogger ScopeBaseLogger(string scope) => _logger.Scoped(scope);

		public Task InitializeAsync()
		{
			// Mock APM server should be started only after sample application is started in clean state.
			// The order is important to prevent agent's queued data from the previous test to be sent
			// to this test instance of mock APM server.
			_iisAdministration.SetupSampleAppInCleanState(_envVarsToSetForSampleAppPool, SampleAppShouldHaveAccessToPerfCounters);
			if (_startMockApmServer)
				_mockApmServer.RunInBackground(_mockApmServerPort);
			else
			{
				_logger.Info()
					?.Log("Not starting mock APM server because startMockApmServer argument to ctor is {startMockApmServer}", _startMockApmServer);
			}

			return Task.CompletedTask;
		}

		public async Task DisposeAsync()
		{
			if (TearDownPersistentData)
				_iisAdministration.DisposeSampleApp();
			else
			{
				_logger.Warning()
					?.Log("Not tearing down IIS sample application and pool because {Reason}", TearDownPersistentDataReason);
			}

			if (_startMockApmServer) await _mockApmServer.StopAsync();

			_logger.Info()?.Log("Finished test: {FullUnitTestName}", GetCurrentTestDisplayName(_xUnitOutputHelper));
		}

		private string GetSampleAppLogFilePath()
		{
			var sampleAppLogFilePath = Environment.GetEnvironmentVariable(LoggingConfig.LogFileEnvVarName);
			if (sampleAppLogFilePath != null)
			{
				_logger.Info()?.Log("Environment variable `{SampleAppLogFileEnvVarName}' is set to `{SampleAppLogFilePath}'"
					+ " - using it to write/read sample application's and agent's log", LoggingConfig.LogFileEnvVarName, sampleAppLogFilePath);
				return sampleAppLogFilePath;
			}

			sampleAppLogFilePath = Path.Combine(Path.GetTempPath(), $"{Consts.SampleApp.AppName}.log");
			_logger.Info()?.Log("Environment variable `{SampleAppLogFileEnvVarName}' is not set"
				+ " - using `{SampleAppLogFilePath}' to write/read sample application's and agent's log",
				LoggingConfig.LogFileEnvVarName, sampleAppLogFilePath);
			return sampleAppLogFilePath;
		}

		private static string BuildApmServerUrl(int apmServerPort) => $"http://localhost:{apmServerPort}/";

		protected async Task<HttpResponseMessage> SendGetRequestToSampleAppAndVerifyResponse(string relativeUrlPath, int expectedStatusCode)
		{
			var httpClient = new HttpClient();
			var url = Consts.SampleApp.RootUrl + "/" + relativeUrlPath;
			_logger.Debug()?.Log("Sending request with URL: {url} and expected status code: {HttpStatusCode}...", url, expectedStatusCode);
			var response = await httpClient.GetAsync(url);
			_logger.Debug()
				?.Log("Request sent. Actual status code: {HttpStatusCode} ({HttpStatusCodeEnum})",
					(int)response.StatusCode, response.StatusCode);
			try
			{
				response.StatusCode.Should().Be(expectedStatusCode);
			}
			catch (XunitException ex)
			{
				var responseContent = await response.Content.ReadAsStringAsync();
				_logger.Error()?.Log("{ExceptionMessage}. Response content:\n{ResponseContent}", ex.Message, responseContent);
				throw;
			}

			var processIdInResponse = response.Headers.GetValues(AspNetFullFrameworkSampleApp.Consts.ProcessIdResponseHeaderName);
			_logger.Debug()?.Log("{ProcessIdHeaderName} in response is {ProcessIdHeaderValue}",
				AspNetFullFrameworkSampleApp.Consts.ProcessIdResponseHeaderName, processIdInResponse);

			var apmServerUrlsInResponse =
				response.Headers?.GetValues(AspNetFullFrameworkSampleApp.Consts.ElasticApmServerUrlsResponseHeaderName).ToList();
			try
			{
				apmServerUrlsInResponse.Should().HaveCount(1);
				apmServerUrlsInResponse.First().Should().Be(BuildApmServerUrl(_mockApmServerPort));
			}
			catch (XunitException ex)
			{
				_logger.Error()?.LogException(ex, "Sample application's APM-server-URLs configuration setting ({ActualApmServerUrl})" +
					" is different from expected ({ExpectedApmServerUrl})",
					string.Join(", ", apmServerUrlsInResponse), BuildApmServerUrl(_mockApmServerPort));

				await PostTestFailureDiagnostics();

				throw;
			}

			return response;
		}

		protected async Task VerifyDataReceivedFromAgent(Action<ReceivedData> verifyAction)
		{
			var attemptNumber = 0;
			var timerSinceStart = Stopwatch.StartNew();
			while (true)
			{
				++attemptNumber;

				if (!_mockApmServer.ReceivedData.InvalidPayloadErrors.IsEmpty)
				{
					var messageBuilder = new StringBuilder();
					messageBuilder.AppendLine("There is at least one invalid payload error - the test is considered as failed.");
					messageBuilder.AppendLine(TextUtils.Indent("Invalid payload error(s):", 1));
					foreach (var invalidPayloadError in _mockApmServer.ReceivedData.InvalidPayloadErrors)
						messageBuilder.AppendLine(TextUtils.Indent(invalidPayloadError, 2));
					throw new XunitException(messageBuilder.ToString());
				}

				try
				{
					verifyAction(_mockApmServer.ReceivedData);
					timerSinceStart.Stop();
					_logger.Debug()
						?.Log("Data received from agent passed verification." +
							" Time elapsed: {VerificationTimeSeconds}s." +
							" Attempt #{AttemptNumber} out of {MaxNumberOfAttempts}",
							timerSinceStart.Elapsed.TotalSeconds,
							attemptNumber, DataSentByAgentVerificationConsts.MaxNumberOfAttemptsToVerify);
					LogSampleAppLogFileContent();
					await LogSampleAppDiagnosticsPage();
					return;
				}
				catch (XunitException ex)
				{
					var logOnThisAttempt =
						attemptNumber >= DataSentByAgentVerificationConsts.LogMessageAfterNInitialAttempts &&
						attemptNumber % DataSentByAgentVerificationConsts.LogMessageEveryNAttempts == 0;

					if (logOnThisAttempt)
					{
						_logger.Warning()
							?.LogException(ex,
								"Data received from agent did NOT pass verification." +
								" Time elapsed: {VerificationTimeSeconds}s." +
								" Attempt #{AttemptNumber} out of {MaxNumberOfAttempts}" +
								" This message is printed only every {LogMessageEveryNAttempts} attempts",
								timerSinceStart.Elapsed.TotalSeconds,
								attemptNumber, DataSentByAgentVerificationConsts.MaxNumberOfAttemptsToVerify,
								DataSentByAgentVerificationConsts.LogMessageEveryNAttempts);
					}

					if (attemptNumber == DataSentByAgentVerificationConsts.MaxNumberOfAttemptsToVerify)
					{
						_logger.Error()?.LogException(ex, "Reached max number of attempts to verify payload - Rethrowing the last exception...");
						await PostTestFailureDiagnostics();
						throw;
					}

					if (logOnThisAttempt)
					{
						_logger.Debug()
							?.Log("Waiting {WaitBetweenVerifyAttemptsMs}ms before the next attempt..." +
								" This message is printed only every {LogMessageEveryNAttempts} attempts",
								DataSentByAgentVerificationConsts.WaitBetweenVerifyAttemptsMs,
								DataSentByAgentVerificationConsts.LogMessageEveryNAttempts);
					}

					Thread.Sleep(DataSentByAgentVerificationConsts.WaitBetweenVerifyAttemptsMs);
				}
				catch (Exception ex)
				{
					_logger.Error()?.LogException(ex, "Exception escaped from verifier");
					throw;
				}
			}
		}

		// ReSharper disable once MemberCanBeProtected.Global
		public static IEnumerable<object[]> AllSampleAppUrlPaths()
		{
			foreach (var data in SampleAppUrlPaths.AllPaths) yield return new object[] { data };
		}

		public static SampleAppUrlPathData RandomSampleAppUrlPath() =>
			SampleAppUrlPaths.AllPaths[RandomGenerator.GetInstance().Next(0, SampleAppUrlPaths.AllPaths.Count)];

		private static string GetCurrentTestDisplayName(ITestOutputHelper xUnitOutputHelper)
		{
			var helper = (TestOutputHelper)xUnitOutputHelper;

			var test = (ITest)helper.GetType()
				.GetField("test", BindingFlags.NonPublic | BindingFlags.Instance)
				.GetValue(helper);
			return test.DisplayName;
		}

		private void LogSampleAppLogFileContent()
		{
			if (!_sampleAppLogEnabled)
			{
				_logger.Info()?.Log("Sample application log is disabled");
				return;
			}

			string sampleAppLogFileContent;
			try
			{
				sampleAppLogFileContent = File.ReadAllText(_sampleAppLogFilePath);
			}
			catch (Exception ex)
			{
				_logger.Info()?.LogException(ex, "Exception thrown while trying to read sample application log file (`{SampleAppLogFilePath}')",
					_sampleAppLogFilePath);
				return;
			}

			_logger.Info()?.Log("Sample application log:\n{SampleAppLogFileContent}", TextUtils.Indent(sampleAppLogFileContent));
		}

		private async Task LogSampleAppDiagnosticsPage()
		{
			var httpClient = new HttpClient();
			const string url = Consts.SampleApp.RootUrl + "/" + DiagnosticsController.DiagnosticsPageRelativePath;
			_logger.Debug()?.Log("Getting content of sample application diagnostics page ({url})...", url);
			var response = await httpClient.GetAsync(url);
			_logger.Debug()?.Log("Received sample application's diagnostics page. Status code: {HttpStatusCode} ({HttpStatusCodeEnum})",
					(int)response.StatusCode, response.StatusCode);

			_logger.Info()?.Log("Sample application's diagnostics page content:\n{DiagnosticsPageContent}",
				TextUtils.Indent(await response.Content.ReadAsStringAsync()));
		}

		private async Task PostTestFailureDiagnostics()
		{
			_iisAdministration.LogIisApplicationHostConfig();
			LogSampleAppLogFileContent();
			await LogSampleAppDiagnosticsPage();

			_logger.Debug()
				?.Log("Analyzing potential issues... _mockApmServer.ReceivedData: " +
					"#transactions: {NumberOfTransactions}, #spans: {NumberOfSpans}, #errors: {NumberOfErrors}, #metric sets: {NumberOfMetricSets}",
					_mockApmServer.ReceivedData.Transactions.Count,
					_mockApmServer.ReceivedData.Spans.Count,
					_mockApmServer.ReceivedData.Errors.Count,
					_mockApmServer.ReceivedData.Metrics.Count);

			FindReceivedDataWithTimestampEarlierThanTestStart();
		}

		private void FindReceivedDataWithTimestampEarlierThanTestStart()
		{
			foreach (var error in _mockApmServer.ReceivedData.Errors) AnalyzeDtoTimestamp(error.Timestamp, error);
			foreach (var metricSet in _mockApmServer.ReceivedData.Metrics) AnalyzeDtoTimestamp(metricSet.Timestamp, metricSet);
			foreach (var span in _mockApmServer.ReceivedData.Spans) AnalyzeDtoTimestamp(span.Timestamp, span);
			foreach (var transaction in _mockApmServer.ReceivedData.Transactions) AnalyzeDtoTimestamp(transaction.Timestamp, transaction);

			void AnalyzeDtoTimestamp(long dtoTimestamp, object dto)
			{
				var dtoStartTime = TimeUtils.TimestampToDateTimeOffset(dtoTimestamp);

				if (_testStartTime <= dtoStartTime) return;

				_logger.Warning()
					?.Log("The following DTO received from the agent has timestamp that is earlier than the current test start time. " +
						"DTO timestamp: {DtoTimestamp}, test start time: {TestStartTime}, DTO: {DtoFromAgent}",
						dtoStartTime.LocalDateTime, _testStartTime.LocalDateTime, dto);
			}
		}

		protected async Task VerifyDataReceivedFromAgent(SampleAppUrlPathData sampleAppUrlPathData) =>
			await VerifyDataReceivedFromAgent(receivedData => { TryVerifyDataReceivedFromAgent(sampleAppUrlPathData, receivedData); });

		protected void TryVerifyDataReceivedFromAgent(SampleAppUrlPathData sampleAppUrlPathData, ReceivedData receivedData)
		{
			FullFwAssertValid(receivedData);

			receivedData.Transactions.Count.Should().Be(sampleAppUrlPathData.TransactionsCount);
			receivedData.Spans.Count.Should().Be(sampleAppUrlPathData.SpansCount);
			receivedData.Errors.Count.Should().Be(sampleAppUrlPathData.ErrorsCount);

			if (receivedData.Transactions.Count == 1)
			{
				var transaction = receivedData.Transactions.First();

				if (transaction.Context != null)
				{
					transaction.Context.Request.Url.Full.Should().Be(Consts.SampleApp.RootUrl + "/" + sampleAppUrlPathData.RelativeUrlPath);

					var questionMarkIndex = sampleAppUrlPathData.RelativeUrlPath.IndexOf('?');
					if (questionMarkIndex == -1)
					{
						transaction.Context.Request.Url.PathName.Should()
							.Be(Consts.SampleApp.RootUrlPath + "/" + sampleAppUrlPathData.RelativeUrlPath);
						transaction.Context.Request.Url.Search.Should().BeNull();
					}
					else
					{
						transaction.Context.Request.Url.PathName.Should()
							.Be(Consts.SampleApp.RootUrlPath + "/" + sampleAppUrlPathData.RelativeUrlPath.Substring(0, questionMarkIndex));
						transaction.Context.Request.Url.Search.Should().Be(sampleAppUrlPathData.RelativeUrlPath.Substring(questionMarkIndex + 1));
					}

					transaction.Context.Response.StatusCode.Should().Be(sampleAppUrlPathData.StatusCode);
				}

				var httpStatusFirstDigit = sampleAppUrlPathData.StatusCode / 100;
				transaction.Result.Should().Be($"HTTP {httpStatusFirstDigit}xx");
				transaction.SpanCount.Started.Should().Be(sampleAppUrlPathData.SpansCount);
			}
		}

		private void FullFwAssertValid(ReceivedData receivedData)
		{
			foreach (var error in receivedData.Errors) FullFwAssertValid(error);
			foreach (var metadata in receivedData.Metadata) FullFwAssertValid(metadata);
			foreach (var metricSet in receivedData.Metrics) FullFwAssertValid(metricSet);
			foreach (var span in receivedData.Spans) FullFwAssertValid(span);
			foreach (var transaction in receivedData.Transactions) FullFwAssertValid(transaction);
		}

		private void FullFwAssertValid(MetadataDto metadata)
		{
			metadata.Should().NotBeNull();

			FullFwAssertValid(metadata.Service);
			FullFwAssertValid(metadata.System);
		}

		private void FullFwAssertValid(Service service)
		{
			service.Should().NotBeNull();

			FullFwAssertValid(service.Framework);

			string expectedServiceName;
			if (AgentConfig.ServiceName == null)
				expectedServiceName = AbstractConfigurationReader.AdaptServiceName($"{Consts.SampleApp.SiteName}_{Consts.SampleApp.AppPoolName}");
			else
				expectedServiceName = AgentConfig.ServiceName;
			service.Name.Should().Be(expectedServiceName);
		}

		private void FullFwAssertValid(Framework framework)
		{
			framework.Should().NotBeNull();

			framework.Name.Should().Be("ASP.NET");
			framework.Version.Should().StartWith("4.");
		}

		private void FullFwAssertValid(Api.System system) => system.Should().BeNull();

		private void FullFwAssertValid(ErrorDto error)
		{
			error.Transaction.AssertValid();
			if (error.Context != null) FullFwAssertValid(error.Context, error);
			error.Culprit.NonEmptyAssertValid();
			error.Exception.AssertValid();
		}

		private void FullFwAssertValid(TransactionDto transaction)
		{
			transaction.Should().NotBeNull();

			if (transaction.Context != null) FullFwAssertValid(transaction.Context, transaction);
			transaction.Name.Should().NotBeNull();
			TransactionResultFullFwAssertValid(transaction.Result);
			transaction.Type.Should().Be(ApiConstants.TypeRequest);
			FullFwAssertValid(transaction.SpanCount);
		}

		private void FullFwAssertValid(SpanCount spanCount)
		{
			spanCount.Should().NotBeNull();

			spanCount.Dropped.Should().Be(0);
		}

		private void FullFwAssertValid(Url url)
		{
			url.Should().NotBeNull();

			url.Full.Should().NotBeNull();
			url.Raw.Should().Be(url.Full);
			url.Protocol.Should().Be("HTTP");
			url.HostName.Should().Be(Consts.SampleApp.Host);
			url.PathName.Should().NotBeNull();
		}

		private void TransactionResultFullFwAssertValid(string result) => result.Should().MatchRegex("HTTP [1-9]xx");

		// ReSharper disable once UnusedParameter.Local
		private void FullFwAssertValid(ContextDto context, TransactionDto _)
		{
			context.Should().NotBeNull();

			FullFwAssertValid(context.Request);
			FullFwAssertValid(context.Response);
		}

		// ReSharper disable once UnusedParameter.Local
		private void FullFwAssertValid(ContextDto context, ErrorDto _)
		{
			context.Should().NotBeNull();

			FullFwAssertValid(context.Request);
		}

		private void FullFwAssertValid(SpanDto span)
		{
			span.Should().NotBeNull();

			FullFwAssertValid(span.StackTrace);
		}

		private void FullFwAssertValid(List<CapturedStackFrame> stackTrace)
		{
			stackTrace.Should().NotBeNull();

			foreach (var stackFrame in stackTrace) FullFwAssertValid(stackFrame);
		}

		private void FullFwAssertValid(CapturedStackFrame capturedStackFrame)
		{
			capturedStackFrame.Should().NotBeNull();

			capturedStackFrame.Function.NonEmptyAssertValid();
		}

		private void FullFwAssertValid(MetricSetDto metricSet)
		{
			metricSet.Should().NotBeNull();

			foreach (var (metricTypeName, _) in metricSet.Samples)
			{
				if (MetricsAssertValid.MetricMetadataPerType[metricTypeName].ImplRequiresAccessToPerfCounters)
				{
					SampleAppShouldHaveAccessToPerfCounters.Should()
						.BeTrue($"Metric {metricTypeName} implementation requires access to performance counters");
				}
			}
		}

		private void FullFwAssertValid(Request request)
		{
			request.Should().NotBeNull();

			FullFwAssertValid(request.Socket);
			FullFwAssertValid(request.Url);

			if (AgentConfig.CaptureHeaders)
			{
				var caseInsensitiveRequestHeaders = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase);
				caseInsensitiveRequestHeaders["Host"].Should().Be(Consts.SampleApp.Host);
			}
			else
				request.Headers.Should().BeNull();
		}

		private void FullFwAssertValid(Socket socket)
		{
			socket.Should().NotBeNull();

			socket.Encrypted.Should().BeFalse();
			socket.RemoteAddress.Should().BeOneOf("::1", "127.0.0.1");
		}

		private void FullFwAssertValid(Response response)
		{
			response.Should().NotBeNull();

			if (AgentConfig.CaptureHeaders)
				response.Headers.Should().NotBeNull();
			else
				response.Headers.Should().BeNull();

			response.Finished.Should().BeTrue();
		}

		protected class AgentConfiguration
		{
			internal bool CaptureHeaders = true;
			internal string ServiceName;
		}

		public class SampleAppUrlPathData
		{
			public readonly int ErrorsCount;
			public readonly string RelativeUrlPath;
			public readonly int SpansCount;
			public readonly int StatusCode;
			public readonly int TransactionsCount;

			public SampleAppUrlPathData(string relativeUrlPath, int statusCode, int transactionsCount = 1, int spansCount = 0, int errorsCount = 0)
			{
				RelativeUrlPath = relativeUrlPath;
				StatusCode = statusCode;
				TransactionsCount = transactionsCount;
				SpansCount = spansCount;
				ErrorsCount = errorsCount;
			}

			public SampleAppUrlPathData Clone(
				string relativeUrlPath = null,
				int? status = null,
				int? transactionsCount = null,
				int? spansCount = null,
				int? errorsCount = null
			) => new SampleAppUrlPathData(
				relativeUrlPath ?? RelativeUrlPath,
				status ?? StatusCode,
				transactionsCount ?? TransactionsCount,
				spansCount ?? SpansCount,
				errorsCount ?? ErrorsCount);
		}
	}
}
