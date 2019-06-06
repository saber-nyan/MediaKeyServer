using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;

namespace KeyServer {
	internal static class Program {
		private const int KeyeventfExtendedkey = 0x0001; //Key down flag
		private const int KeyeventfKeyup = 0x0002; //Key up flag

		[DllImport("user32.dll", SetLastError = true)]
		private static extern void keybd_event(byte bVk, byte bScan, int dwFlags, int dwExtraInfo);

		/**
		 * Keycodes: https://docs.microsoft.com/ru-ru/windows/desktop/inputdev/virtual-key-codes
		 * Before starting, execute as admin (for russian locale): netsh http add urlacl url=http://+:8089/ user=Все
		 */
		public static void Main(string[] args) {
			Console.WriteLine("Starting server...");
			if (!HttpListener.IsSupported) {
				Console.WriteLine("Server is not supported, aborting!");
				return;
			}

			const string listenAddress = "http://+:8089";
			var server = new HttpListener();
			server.Prefixes.Add($"{listenAddress}/pressKey/");
			server.Start();
			Console.WriteLine("Server started!");

			while (true) {
				var context = server.GetContext();
				var request = context.Request;
				var requestString = new StreamReader(request.InputStream).ReadToEnd();

				Console.WriteLine($"Got request {requestString}");

				var response = context.Response;
				byte[] responseBuffer;
				try {
					var json = JObject.Parse(requestString);
					var keyName = Convert.FromBase64String(json["key"].ToString())[0];
					Console.WriteLine($"Pressing key '{keyName}'...");
					keybd_event(keyName, 0, KeyeventfExtendedkey, 0);
					keybd_event(keyName, 0, KeyeventfKeyup, 0);


					var oJson = new JObject {
						["success"] = true
					};
					responseBuffer = Encoding.UTF8.GetBytes(oJson.ToString());
				}
				catch (Exception e) {
					Console.WriteLine($"Error while parsing: {e}");
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
	}
}
