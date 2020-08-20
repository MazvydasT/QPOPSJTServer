using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

using vtortola.WebSockets;

namespace QPOPSJTServer
{
    class Context
    {
        public WebSocket Client { get; set; }
        public Message RequestMessage { get; set; }
    }

    class Message
    {
        public long Id { get; set; }
        public Command Command { get; set; }
        public string Data { get; set; }
        public string Error { get; set; }
    }

    class Command
    {
        public string Name { get; set; }
        public Dictionary<string, string> Arguments { get; set; }
    }

    class Program
    {
        const int PORT = 9876;

        static void Main(string[] args)
        {
            var server = new WebSocketListener(new IPEndPoint(IPAddress.Loopback, PORT));
            server.Standards.RegisterStandard(new WebSocketFactoryRfc6455());
            server.StartAsync();

            WaitForClient(server);

            Application.Run();

            server.Stop();
        }

        static void WaitForClient(WebSocketListener server)
        {
            Debug.WriteLine("Waiting for client");

            server.AcceptWebSocketAsync(new CancellationToken()).ContinueWith(task =>
            {
                Debug.WriteLine("Client received");

                WaitForClient(server);

                WaitForMessage(task.Result);
            });
        }

        static void WaitForMessage(WebSocket client)
        {
            Debug.WriteLine("Waiting for message");

            client.ReadStringAsync(new CancellationToken()).ContinueWith(task =>
            {
                if (!client.IsConnected)
                {
                    Debug.WriteLine("Client disconnected");
                    return;
                }

                Debug.WriteLine("Message received");

                WaitForMessage(client);

                ProcessMessage(task.Result, client);
            });
        }

        static void ProcessMessage(string messageText, WebSocket client)
        {
            var message = JsonConvert.DeserializeObject<Message>(messageText);

            var context = new Context()
            {
                Client = client,
                RequestMessage = message
            };

            switch (message.Command.Name)
            {
                case "getVersion":
                    Send(Assembly.GetEntryAssembly().GetName().Version.ToString(), context);
                    break;

                case "convertAjtToJt":
                    AJT2JT(context);
                    break;

                default:
                    HandleError("Invalid command.", context);
                    break;
            }
        }

        static void AJT2JT(Context context)
        {
            var arguments = context.RequestMessage?.Command?.Arguments ?? new Dictionary<string, string>();

            var pathToAsciiToJtConverter = arguments.TryGetValue("ajt2jt", out string path) ? path : null;

            if (string.IsNullOrWhiteSpace(pathToAsciiToJtConverter) || !File.Exists(pathToAsciiToJtConverter))
            {
                HandleError($"Path '{pathToAsciiToJtConverter}' to AJT to JT converter is not valid.", context);
                return;
            }

            var ajtSource = arguments.TryGetValue("ajtSource", out string source) ? source : null;

            if (string.IsNullOrWhiteSpace(ajtSource))
            {
                HandleError($"No AJT data provided.", context);
                return;
            }

            string ajtFile = null;
            string jtFile = null;

            try
            {
                ajtFile = Path.GetTempFileName();
                jtFile = Path.ChangeExtension(ajtFile, ".jt");

                File.WriteAllText(ajtFile, ajtSource);

                var process = Process.Start(new ProcessStartInfo()
                {
                    FileName = pathToAsciiToJtConverter,
                    Arguments = $"\"{ajtFile}\" \"{jtFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                });

                var error = process.StandardError.ReadToEnd().Trim();

                if (!string.IsNullOrWhiteSpace(error))
                    throw new Exception(error);

                process.WaitForExit();

                Send(Convert.ToBase64String(File.ReadAllBytes(jtFile)), context);
            }

            catch (Exception e)
            {
                HandleError(e.Message, context);
            }

            finally
            {
                try
                {
                    if (File.Exists(ajtFile))
                        File.Delete(ajtFile);

                    if (File.Exists(jtFile))
                        File.Delete(jtFile);
                }

                catch (Exception) { }
            }
        }

        static void Send(string data, Context context, bool errorMessage = false)
        {
            var message = new Message()
            {
                Id = context.RequestMessage.Id
            };

            if (errorMessage)
                message.Error = data;

            else
                message.Data = data;

            context.Client.WriteString(JsonConvert.SerializeObject(message));
        }

        static void HandleError(string errorMessage, Context context)
        {
            Send(errorMessage, context, true);
        }
    }
}
