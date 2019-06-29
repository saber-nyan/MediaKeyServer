using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Control;
using CommandLine;
using Newtonsoft.Json.Linq;
using NLog;

namespace KeyServer {
	internal static class Program {
		private const int KeyeventfExtendedkey = 0x0001; //Key down flag
		private const int KeyeventfKeyup = 0x0002; //Key up flag
		private const string Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
		private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
		private static readonly Random Random = new Random();

		[DllImport("user32.dll", SetLastError = true)]
		private static extern void keybd_event(byte bVk, byte bScan, int dwFlags, int dwExtraInfo);

		/**
		 * Keycodes: https://docs.microsoft.com/ru-ru/windows/desktop/inputdev/virtual-key-codes
		 * Before starting, execute as admin (for russian locale): netsh http add urlacl url=http://+:8089/ user=Все
		 */
		public static void Main(string[] args) {
			Parser.Default.ParseArguments<Options>(args)
				.WithParsed(o => {
					string authToken;
					if (string.IsNullOrEmpty(o.Token)) {
						authToken = new string(Enumerable.Repeat(Chars, 16)
							.Select(s => s[Random.Next(s.Length)]).ToArray());
						Logger.Info($"Generated new token: {authToken}");
					}
					else {
						authToken = o.Token;
					}

					StartServer(authToken, o.Url, o.Port).Wait();
				});
		}

		private static async Task StartServer(string authToken, string url, int port) {
			Logger.Info("Starting server...\n" +
			            "Please remember to execute `netsh http add urlacl url=http://+:8089/ user=EVERYONE`");
			if (!HttpListener.IsSupported) {
				Logger.Fatal("Server is not supported, aborting!");
				return;
			}

			var listenAddress = $"{url}:{port}";
			var server = new HttpListener();
			server.Prefixes.Add($"{listenAddress}/pressKey/");
			server.Start();
			Logger.Info($"Server started on {listenAddress}!");

			while (true) {
				var context = server.GetContext();
				var request = context.Request;
				var requestString = new StreamReader(request.InputStream).ReadToEnd();

				Logger.Debug($"Got request {requestString}");

				var response = context.Response;
				byte[] responseBuffer;
				try {
					var json = JObject.Parse(requestString);
					if (json["token"].ToString() != authToken) throw new SecurityException("Invalid auth token.");

					var action = json["action"].ToString();
					switch (action) {
						case "pressKey": {
							var keyName = Convert.FromBase64String(json["key"].ToString())[0];
							Logger.Trace($"Pressing key '{keyName}'...");
							keybd_event(keyName, 0x45, KeyeventfExtendedkey, 0);
							Thread.Sleep(100);
							keybd_event(keyName, 0x45, KeyeventfKeyup, 0);

							var oJson = new JObject {
								["success"] = true
							};
							responseBuffer = Encoding.UTF8.GetBytes(oJson.ToString());
							break;
						}

						case "getInfo": {
							var sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
							var mediaProperties = await sessionManager.GetCurrentSession().TryGetMediaPropertiesAsync();

							var oJson = new JObject {
								["success"] = true,
								["title"] = mediaProperties.Title
							};
							responseBuffer = Encoding.UTF8.GetBytes(oJson.ToString());
							break;
						}

						default: {
							throw new Exception($"Unknown action {action}");
						}
					}
				}
				catch (Exception e) {
					Logger.Warn(e, "Failed to parse JSON!");
					var oJson = new JObject {
						["success"] = false,
						["errorName"] = e.GetType().ToString(),
						["errorDescription"] = e.ToString()
					};
					responseBuffer = Encoding.UTF8.GetBytes(oJson.ToString());
				}

				response.ContentLength64 = responseBuffer.Length;

				var outStream = response.OutputStream;
				outStream.Write(responseBuffer, 0, responseBuffer.Length);
				outStream.Close();
			}
		}

		// ReSharper disable once ClassNeverInstantiated.Local
		[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Local")]
		private class Options {
			[Option('u', "url", Default = "http://+", HelpText = "Listen url.")]
			public string Url { get; set; }

			[Option('p', "port", Default = 8089, HelpText = "Listen port.")]
			public int Port { get; set; }

			[Option('t', "token", Required = false, HelpText = "Auth token. Will be generated if not set.")]
			public string Token { get; set; }
		}
	}
}
