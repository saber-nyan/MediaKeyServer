using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security;
using System.Text;
using System.Threading;
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
		private const string Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

		private const int KeyeventKeydown = 0x0001; //Key down flag
		private const int KeyeventKeyup = 0x0002; //Key up flag
		private static Logger _logger;
		private static readonly CoreAudioDevice DefaultAudioDevice;

		static Program() {
			DefaultAudioDevice = new CoreAudioController().DefaultPlaybackDevice;
		}

		[DllImport("user32.dll", SetLastError = true)]
		private static extern void keybd_event(byte bVk, byte bScan, int dwFlags, int dwExtraInfo);

		public static void Main(string[] args) {
			var logConfig = new LoggingConfiguration();
			var logFile = new FileTarget("logFile") {
				FileName = "KeyServer.log",
				ArchiveAboveSize = 2000000,
				EnableArchiveFileCompression = true,
				Encoding = Encoding.UTF8,
				KeepFileOpen = true,
				WriteBom = false,
				Layout =
					@"${longdate}|${level:uppercase=true}|${logger}|${callsite:methodName=false:className=false:fileName=true:includeSourcePath=false}|${message}"
			};
			var logConsole = new ColoredConsoleTarget("logConsole") {
				EnableAnsiOutput = false,
				ErrorStream = true,
				DetectConsoleAvailable = true,
				DetectOutputRedirected = false,
				AutoFlush = true,
				Layout =
					@"${time} ${level:uppercase=true} ${callsite:methodName=false:className=false:fileName=true:includeSourcePath=false} ${message}"
			};
			logConfig.AddRule(LogLevel.Debug, LogLevel.Fatal, logConsole);
			logConfig.AddRule(LogLevel.Trace, LogLevel.Fatal, logFile);

			LogManager.Configuration = logConfig;
			_logger = LogManager.GetCurrentClassLogger();

			Parser.Default.ParseArguments<Options>(args)
				.WithParsed(o => {
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

					StartServer(accessToken, o.Url, o.Port).Wait();
				});
		}

		private static async Task StartServer(string accessToken, string url, int port) {
			if (!HttpListener.IsSupported) {
				_logger.Fatal("Server is not supported, aborting!");
				return;
			}

			var listenAddress = $"{url}:{port}";
			_logger.Info("Starting server...");
			_logger.Info(
				"Remember to execute `netsh http add url.acl url={listenUrl}/ user=EVERYONE`! (or `user=Все` in russian locale)",
				listenAddress);
			var server = new HttpListener();
			server.Prefixes.Add($"{listenAddress}/mediaApi/");
			server.Start();

			_logger.Info("Server started on {listenAddress}", listenAddress);

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
					_logger.Error(e, "Unexpected exception occured!");
					result = new JObject {
						["success"] = false,
						["error"] = e.GetType().ToString(),
						["errorDescription"] = e.ToString()
					};
				}

				var responseBuffer = Encoding.UTF8.GetBytes(result.ToString());
				response.ContentLength64 = responseBuffer.LongLength;

				var outStream = response.OutputStream;
				await outStream.WriteAsync(responseBuffer, 0, responseBuffer.Length);
				outStream.Close();
				outStream.Dispose();
			}
		}

		private static async Task<JObject> ProcessRequest(
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

		private static async Task PerformAction(JObject request) {
			var action = request["action"]?.ToString();
			switch (action) {
				case "prev": {
					PressKey(0xB1);
					break;
				}
				case "pause": {
					PressKey(0xB3);
					break;
				}
				case "next": {
					PressKey(0xB0);
					break;
				}
				case "volDown": {
					PressKey(0xAE);
					break;
				}
				case "mute": {
					PressKey(0xAD);
					break;
				}
				case "volUp": {
					PressKey(0xAF);
					break;
				}
				case "setVol": {
					var newVol = (double?) request["newVol"];
					if (newVol == null) throw new MalformedJsonException("JSON has no `newVol` field");

					await DefaultAudioDevice
						.SetVolumeAsync((double) newVol); // Strange cast, we already checked for null...
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

		private static async Task<JObject> GetInfo() {
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

			var volume = await DefaultAudioDevice.GetVolumeAsync();

			var resultObj = new JObject {
				["success"] = true,
				["title"] = mediaProperties?.Title,
				["volume"] = volume,
				["muted"] = DefaultAudioDevice.IsMuted,
				["preview"] = bytes != null ? Convert.ToBase64String(bytes) : null
			};
			_logger.Debug("Got result: {json}", resultObj);

			return resultObj;
		}

		private static void PressKey(byte keyCode) {
			_logger.Trace("Pressing key {keyCode}", keyCode);
			keybd_event(keyCode, 0x45, KeyeventKeydown, 0);
			Thread.Sleep(50);
			keybd_event(keyCode, 0x45, KeyeventKeyup, 0);
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
