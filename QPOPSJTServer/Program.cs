using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
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
        public int Id { get; set; }
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

            server.AcceptWebSocketAsync(CancellationToken.None).ContinueWith(task =>
            {
                WaitForClient(server);

                var client = task.Result;

                Debug.WriteLine($"Client received {client.RemoteEndpoint}");

                Context context = new Context()
                {
                    Client = client
                };

                WaitForMessage(context);
            });
        }

        static void WaitForMessage(Context context)
        {
            Debug.WriteLine("Waiting for message");

            var client = context.Client;

            client.ReadStringAsync(CancellationToken.None).ContinueWith(task =>
            {
                if (!client.IsConnected)
                {
                    Debug.WriteLine($"Client disconnected {client.RemoteEndpoint}");
                    return;
                }

                var message = JsonConvert.DeserializeObject<Message>(task.Result);

                Debug.WriteLine($"Message received {message.Id}");

                WaitForMessage(context);
                
                ProcessMessage(message, context);
            });
        }

        static void ProcessMessage(Message message, Context context)
        {
            context.RequestMessage = message;

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

                using(var fileStream = File.OpenRead(jtFile))
                {
                    Send(fileStream, context);
                }
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

        static void Send(Stream dataStream, Context context, bool errorMessage = false)
        {
            var idByteArray = BitConverter.GetBytes(context.RequestMessage.Id);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(idByteArray);

            var isError = (byte)(errorMessage ? 1 : 0);

            lock (context.Client)
            {
                using (var messageWriterStream = context.Client.CreateMessageWriter(WebSocketMessageType.Binary))
                {
                    messageWriterStream.Write(idByteArray, 0, 4);
                    messageWriterStream.Write(new byte[] { isError }, 0, 1);
                    dataStream.CopyTo(messageWriterStream);
                }
            }

            Debug.WriteLine($"Message sent {context.RequestMessage.Id}");
        }


        static void Send(byte[] data, Context context, bool errorMessage = false)
        {
            using (var memoryStream = new MemoryStream(data))
            {
                Send(memoryStream, context, errorMessage);
            }
        }

        private static Encoding utf8EncodingWithoutBOM = new UTF8Encoding(false);
        static void Send(string data, Context context, bool errorMessage = false)
        {
            Send(utf8EncodingWithoutBOM.GetBytes(data), context, errorMessage);
        }

        static void HandleError(string errorMessage, Context context)
        {
            Send(errorMessage, context, true);
        }
    }
}
