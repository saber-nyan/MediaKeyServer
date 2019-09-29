using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Media.Control;
using AudioSwitcher.AudioApi.CoreAudio;
using CommandLine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace KeyServerNew {
	internal static class Program {
		public static void Main(string[] args) {
			ServiceBase.Run(new MediaKeyService(args));
		}
	}

	internal class MediaKeyService : ServiceBase {
		[SuppressMessage("ReSharper", "ALL")]
		public enum ServiceState {
			SERVICE_STOPPED = 0x00000001,
			SERVICE_START_PENDING = 0x00000002,
			SERVICE_STOP_PENDING = 0x00000003,
			SERVICE_RUNNING = 0x00000004,
			SERVICE_CONTINUE_PENDING = 0x00000005,
			SERVICE_PAUSE_PENDING = 0x00000006,
			SERVICE_PAUSED = 0x00000007
		}

		private const string Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
		private readonly IEnumerable<string> _args;

		private readonly CoreAudioDevice _defaultAudioDevice;
		private Logger _logger;

		public MediaKeyService(IEnumerable<string> args) {
			_defaultAudioDevice = new CoreAudioController().DefaultPlaybackDevice;
			_args = args;
		}

		[DllImport("advapi32.dll", SetLastError = true)]
		private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus serviceStatus);

		protected override void OnStart(string[] arg) {
			base.OnStart(arg);
			var serviceStatus = new ServiceStatus {
				dwCurrentState = ServiceState.SERVICE_START_PENDING,
				dwWaitHint = 3000
			};
			SetServiceStatus(ServiceHandle, ref serviceStatus);
			InitServer();
		}

		protected override void OnStop() {
			base.OnStop();
			_logger.Info("Stopping...");
		}

		private void InitServer() {
			var logConfig = new LoggingConfiguration();
//			var logFile = new FileTarget("logFile") {
//				FileName = "KeyServer.log",
//				ArchiveAboveSize = 2000000,
//				EnableArchiveFileCompression = true,
//				Encoding = Encoding.UTF8,
//				KeepFileOpen = true,
//				WriteBom = false,
//				Layout =
//					@"${longdate}|${level:uppercase=true}|${logger}|${callsite:methodName=false:className=false:fileName=true:includeSourcePath=false}|${message}"
//			};
			var logConsole = new ColoredConsoleTarget("logConsole") {
				EnableAnsiOutput = false,
				ErrorStream = true,
				DetectConsoleAvailable = true,
				DetectOutputRedirected = false,
				AutoFlush = true,
				Layout =
					@"${time} ${level:uppercase=true} ${callsite:methodName=false:className=false:fileName=true:includeSourcePath=false} ${message}"
			};
			var logSystem = new EventLogTarget("logSystem") {
				Layout =
					@"${longdate}|${level:uppercase=true}|${logger}|${callsite:methodName=false:className=false:fileName=true:includeSourcePath=false}|${message}"
			};
			logConfig.AddRule(LogLevel.Trace, LogLevel.Fatal, logConsole);
//			logConfig.AddRule(LogLevel.Debug, LogLevel.Fatal, logFile);
			logConfig.AddRule(LogLevel.Debug, LogLevel.Fatal, logSystem);

			LogManager.Configuration = logConfig;
			_logger = LogManager.GetCurrentClassLogger();

			Parser.Default.ParseArguments<Options>(_args)
				.WithParsed(async o => {
					_logger.Info("Starting...");
					_logger.Debug("Arguments parsed: {arguments}", o);
					string accessToken;
					if (string.IsNullOrWhiteSpace(o.AccessToken)) {
						var random = new Random();
						accessToken = new string(Enumerable
							.Repeat(Chars, 16)
							.Select(s => s[random.Next(s.Length)]).ToArray()
						);
						_logger.Info("Access token is not specified; generated: {token}", accessToken);
					}
					else {
						accessToken = o.AccessToken;
					}

					await StartServer(accessToken, o.Url, o.Port);
				});
		}

		private async Task StartServer(string accessToken, string url, int port) {
			if (!HttpListener.IsSupported) {
				_logger.Fatal("Server is not supported, aborting!");
				return;
			}

			var listenAddress = $"{url}:{port}/";
			_logger.Info("Starting server...");
			_logger.Info(
				"Remember to execute `netsh http add urlacl url=\"{listenUrl}\" user=EVERYONE`! (or `user=Все` in russian locale)",
				listenAddress);
			var server = new HttpListener();
			server.Prefixes.Add($"{listenAddress}mediaApi/");
			server.Start();

			_logger.Info("Server started on {listenAddress}", listenAddress);

			var serviceStatus = new ServiceStatus {
				dwCurrentState = ServiceState.SERVICE_RUNNING,
				dwWaitHint = 3000
			};
			SetServiceStatus(ServiceHandle, ref serviceStatus);

			while (true) {
				var context = await server.GetContextAsync();
				var request = context.Request;
				var response = context.Response;
				JObject result;
				try {
					result = await ProcessRequest(request, accessToken);
				}
				catch (Exception e) when (e is MalformedJsonException
				                          || e is SecurityException
				                          || e is UnknownMethodException
				                          || e is InvalidOperationException) {
					result = new JObject {
						["success"] = false,
						["error"] = e.GetType().Name,
						["errorDescription"] = e.Message
					};
				}
				catch (Exception e) {
					_logger.Error(e, "SHIT! Unexpected exception occured!\nTrace: {trace}", e.ToString());
					result = new JObject {
						["success"] = false,
						["error"] = e.GetType().ToString(),
						["errorDescription"] = e.ToString()
					};
				}

				byte[] responseBuffer = Encoding.UTF8.GetBytes(result.ToString());
				response.ContentLength64 = responseBuffer.LongLength;

				var outStream = response.OutputStream;
				await outStream.WriteAsync(responseBuffer, 0, responseBuffer.Length);
				outStream.Close();
				outStream.Dispose();
			}
		}

		private async Task<JObject> ProcessRequest(
			HttpListenerRequest request,
			string validAccessToken) {
			if (request.HttpMethod != "POST") {
				_logger.Warn("Incorrect HTTP verb ({actualVerb})", request.HttpMethod);
				throw new InvalidOperationException("Non-POST requests are not supported");
			}

			var requestString = await new StreamReader(request.InputStream).ReadToEndAsync();

			_logger.Debug("Got request: {request}", requestString);

			JObject json;
			try {
				json = JObject.Parse(requestString);
			}
			catch (JsonReaderException e) {
				_logger.Warn(e, "Got malformed request: {requestString}", requestString);
				throw new MalformedJsonException("Request JSON cannot be parsed", e);
			}

			var accessToken = json["token"]?.ToString();
			if (string.IsNullOrEmpty(accessToken) || accessToken != validAccessToken) {
				_logger.Warn("Unauthorized access attempt");
				throw new SecurityException("Access token is invalid");
			}

			var method = json["method"]?.ToString();
			switch (method) {
				case "performAction": {
					await PerformAction(json);
					return await GetInfo();
				}

				case "getInfo": {
					return await GetInfo();
				}

				default: {
					_logger.Warn("Unknown method {method}", method);
					throw new UnknownMethodException("Method is unknown");
				}
			}

//			return null; // Never happens
		}

		private async Task PerformAction(JObject request) {
			var action = request["action"]?.ToString();
			switch (action) {
				case "prev": {
//					PressKey(0xB1);
					var sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
					var currentSession = sessionManager.GetCurrentSession();
					await currentSession.TrySkipPreviousAsync();
					break;
				}
				case "pause": {
//					PressKey(0xB3);
					var sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
					var currentSession = sessionManager.GetCurrentSession();
					await currentSession.TryTogglePlayPauseAsync();
					break;
				}
				case "next": {
//					PressKey(0xB0);
					var sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
					var currentSession = sessionManager.GetCurrentSession();
					await currentSession.TrySkipNextAsync();
					break;
				}
				case "volDown": {
//					PressKey(0xAE);
					await _defaultAudioDevice.SetVolumeAsync(_defaultAudioDevice.Volume - 2);
					break;
				}
				case "mute": {
//					PressKey(0xAD);
					await _defaultAudioDevice.SetMuteAsync(!_defaultAudioDevice.IsMuted);
					break;
				}
				case "volUp": {
//					PressKey(0xAF);
					await _defaultAudioDevice.SetVolumeAsync(_defaultAudioDevice.Volume + 2);
					break;
				}
				case "setVol": {
					var newVol = (double?) request["newVol"];
					if (newVol == null) throw new MalformedJsonException("JSON has no `newVol` field");

					await _defaultAudioDevice
						.SetVolumeAsync(newVol.Value);
					break;
				}
				case "hibernate": {
					_logger.Info("Hibernating, bye-bye...");
					Application.SetSuspendState(PowerState.Hibernate, false, false);
					break;
				}
				default: {
					_logger.Warn("Unknown action {action}", action);
					throw new UnknownMethodException("Action is unknown");
				}
			}
		}

		private async Task<JObject> GetInfo() {
			var sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
			var currentSession = sessionManager.GetCurrentSession();
			GlobalSystemMediaTransportControlsSessionMediaProperties mediaProperties = null;
			byte[] bytes = null;
			if (currentSession == null) {
				_logger.Debug("Nothing is playing!");
			}
			else {
				mediaProperties = await currentSession.TryGetMediaPropertiesAsync();
				var previewStream = await mediaProperties.Thumbnail.OpenReadAsync();

				bytes = new byte[previewStream.Size];
				await previewStream.AsStream().ReadAsync(bytes, 0, (int) previewStream.Size);
			}

			var volume = await _defaultAudioDevice.GetVolumeAsync();

			var resultObj = new JObject {
				["success"] = true,
				["title"] = mediaProperties?.Title,
				["volume"] = volume,
				["muted"] = _defaultAudioDevice.IsMuted,
				["preview"] = bytes != null ? Convert.ToBase64String(bytes) : null
			};
			_logger.Trace("Got result: {json}", resultObj);

			return resultObj;
		}

		[SuppressMessage("ReSharper", "ALL")]
		[StructLayout(LayoutKind.Sequential)]
		public struct ServiceStatus {
			public int dwServiceType;
			public ServiceState dwCurrentState;
			public int dwControlsAccepted;
			public int dwWin32ExitCode;
			public int dwServiceSpecificExitCode;
			public int dwCheckPoint;
			public int dwWaitHint;
		}

		[SuppressMessage("ReSharper", "ClassNeverInstantiated.Local")]
		[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Local")]
		[SuppressMessage("ReSharper", "MemberCanBePrivate.Local")]
		private class Options {
			[Option('u', "url", Default = "http://+", HelpText = "Listen on url.")]
			public string Url { get; set; }

			[Option('p', "port", Default = 8090, HelpText = "Listen on port.")]
			public int Port { get; set; }

			[Option('t', "token", Required = false, HelpText = "Access token. Will be generated if not specified.")]
			public string AccessToken { get; set; }

			public override string ToString() {
				return $"{nameof(Url)}: {Url}, {nameof(Port)}: {Port}, {nameof(AccessToken)}: {AccessToken}";
			}
		}

		[Serializable]
		private class MalformedJsonException : ApplicationException {
			public MalformedJsonException(string message) : base(message) { }
			public MalformedJsonException(string message, Exception cause) : base(message, cause) { }

			protected MalformedJsonException(SerializationInfo info,
				StreamingContext context) : base(info, context) { }
		}

		[Serializable]
		private class UnknownMethodException : ApplicationException {
			public UnknownMethodException(string message) : base(message) { }

			protected UnknownMethodException(SerializationInfo info,
				StreamingContext context) : base(info, context) { }
		}
	}
}
