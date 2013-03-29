using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Text.RegularExpressions;
using System.IO;

namespace warpNET
{
    class Server
    {
        Regex RHost = new Regex("(^:+):([0-9]{1,5})");
        Regex RContentLength = new Regex("\r\nContent-Length: ([0-9]+)\r\n");
        Regex RProxyConnection = new Regex("\r\nProxy-Connection: (.+)\r\n");
        Regex RConnection = new Regex("\r\nConnection: (.+)\r\n");

        IPAddress hostname;
        Int32 port;
        Queue<Object> queue = new Queue<Object>();
        public Server(IPAddress hostname, Int32 port)
        {
            this.hostname = hostname;
            this.port = port;
        }

        public void Start()
        {
            Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            socket.NoDelay = true;
            try
            {
                socket.Bind(new System.Net.IPEndPoint(hostname, port));
            }
            catch
            {
                Console.WriteLine(String.Format("!!! Failed to bind server at [{0}:{1}]", hostname, port));
                return;
            }
            Console.WriteLine(String.Format("Server binded at [{0}:{1}].", hostname, port));
            socket.Listen(100);

            while (true)
            {
                Request(socket.Accept());
            }
        }

        public async void Request(Socket socket)
        {
            await Task.Run(async delegate()
            {
                String proxySentString = "";
                String proxyRecievedString = "";

                Console.WriteLine("New task accepted");// (socket.RemoteEndPoint as IPEndPoint).Address);
                String streamstr = "";
                MemoryStream contentStream = new MemoryStream();
                //try
                //{
                while (true)
                {
                    Byte[] buffer = new Byte[1024];
                    //List<ArraySegment<Byte>> buffer = new List<ArraySegment<Byte>>();
                    socket.Receive(buffer);
                    String data = Encoding.UTF8.GetString(buffer);
                    streamstr += data.TrimEnd('\0');
                    if (data.Contains("\r\n\r\n"))
                        break;
                }
                Match lengthm = RContentLength.Match(streamstr);
                String content = "";
                if (lengthm.Groups.Count > 0 && lengthm.Groups[0].Value.Length != 0)
                {
                    Int32 length = Convert.ToInt32(lengthm.Groups[0].Value.Substring(18).TrimEnd('\r', '\n'));
                    content = streamstr.Split(new String[] { "\r\n\r\n" }, StringSplitOptions.None)[1];
                    while (length != content.Length)
                    {
                        Byte[] buffer = new Byte[1024];
                        socket.Receive(buffer);
                        streamstr += Encoding.UTF8.GetString(buffer).TrimEnd('\0');
                    }
                    streamstr = streamstr.Split(new String[] { "\r\n\r\n" }, StringSplitOptions.None)[0] + "\r\n\r\n" + content;
                }
                //}
                //catch
                //{

                //}

                Match proxym = RProxyConnection.Match(streamstr);
                if (proxym.Groups.Count == 0)
                {
                    Console.WriteLine("!!! Task rejected");
                    return;
                }

                String[] requests = streamstr.TrimEnd('\r', '\n').Split(new String[] { "\r\n" }, StringSplitOptions.None);
                if (requests.Length < 2)
                {
                    Console.WriteLine("!!! Task rejected");
                    return;
                }

                String[] heads = requests[0].Split(' ');
                String proxyHost = String.Empty;
                List<String> sRequests = new List<String>();
                for (Int32 i = 1; i < requests.Length; i++)
                {
                    if (requests[i].Contains("Host: "))
                        proxyHost = requests[i].Substring(6);
                    else if (!requests[i].Contains("Proxy-Connection"))
                        sRequests.Add(requests[i]);
                }

                Match connectionm = RConnection.Match(streamstr);
                if (connectionm.Groups.Count == 0)
                    sRequests.Add(String.Format("Connection: {0}", connectionm.Groups[0].Value));
                else
                    sRequests.Add("Connection: close");

                String path = new Uri(heads[1]).PathAndQuery;

                Console.WriteLine(String.Format("Process - {0}", requests[0]));

                String newHead = String.Join(" ", heads[0], path, heads[2]);

                Match hostm = RHost.Match(proxyHost);
                String host;
                UInt16 port;
                if (hostm.Groups.Count > 0 && hostm.Groups[0].Value.Length != 0)
                {
                    host = hostm.Groups[0].Value;
                    port = Convert.ToUInt16(hostm.Groups[1].Value);
                }
                else
                {
                    host = proxyHost;
                    port = 80;
                    proxyHost = proxyHost + ":80";
                }


                try
                {
                    Socket requestSocket = new Socket(SocketType.Stream, ProtocolType.IP);
                    requestSocket.Connect(host, port);

                    requestSocket.Send(Encoding.UTF8.GetBytes(newHead + "\r\n"));
                    proxySentString += newHead + "\r\n";

                    await Task.Delay(200);

                    requestSocket.Send(Encoding.UTF8.GetBytes("Host: "));
                    proxySentString += "Host: ";
                    {
                        Random r = new Random();
                        String remaining = proxyHost;
                        Int32 i = 1;
                        while (remaining.Length > 0)
                        {
                            await Task.Delay(r.Next(2, 4) * 100);
                            if (remaining.Length > i)
                            {
                                requestSocket.Send(Encoding.UTF8.GetBytes(remaining.Substring(0, i)));
                                proxySentString += remaining.Substring(0, i);
                                remaining = remaining.Substring(i);
                            }
                            else
                            {
                                requestSocket.Send(Encoding.UTF8.GetBytes(remaining));
                                proxySentString += remaining;
                                remaining = String.Empty;
                            }
                            i = r.Next(2, 5);
                        }
                    }
                    requestSocket.Send(Encoding.UTF8.GetBytes("\r\n"));
                    proxySentString += "\r\n";
                    requestSocket.Send(Encoding.UTF8.GetBytes(String.Join("\r\n", sRequests)));
                    proxySentString += String.Join("\r\n", sRequests);
                    requestSocket.Send(Encoding.UTF8.GetBytes("\r\n\r\n"));
                    proxySentString += "\r\n\r\n";
                    requestSocket.Send(Encoding.UTF8.GetBytes(content));
                    proxySentString += content;
                    //}
                    //catch
                    //{

                    //}

                    while (true)
                    {
                        Byte[] buffer = new Byte[1024];
                        requestSocket.Receive(buffer);
                        String data = Encoding.UTF8.GetString(buffer).TrimEnd('\0');
                        if (data.Length > 0)
                        {
                            proxyRecievedString += data;
                            socket.Send(Encoding.UTF8.GetBytes(data));
                        }
                        else
                            break;
                    }
                    requestSocket.Close();
                    socket.Close();
                }
                catch (SocketException e)
                {
                    Console.WriteLine(e.Message);
                }

                Console.WriteLine("Task done");

                System.Diagnostics.Debug.WriteLine(
                    "---ChunkStart---\r\n"
                    + "---Requested---\r\n"
                    + streamstr
                    + "------\r\n"
                    + "---Sent---\r\n"
                    + proxySentString
                    + "------\r\n"
                    + "---Received---\r\n"
                    + proxyRecievedString + "\r\n"
                    + "------\r\n"
                    + "---ChunkEnd--");
                //String path = heads[1].Substring(pro
            });
        }
    }

    class Program
    {
        static String findOptionValue(String optionname, String defaultValue, String[] args)
        {
            List<String> argsList = args.ToList();
            Int32 foundIndex = argsList.FindIndex((String s) => { if ((s[0] == '-' || s[0] == '/') && s.Substring(1) == optionname) return true; else return false; });
            if (foundIndex == -1)
                return defaultValue;
            if (argsList.Count > foundIndex + 1)
                return argsList[foundIndex + 1];
            else
                return defaultValue;
        }

        static void Main(string[] args)
        {
            IPAddress host;
            UInt16 port;
            try { host = IPAddress.Parse(findOptionValue("host", "[::1]", args)); }
            catch { Console.WriteLine("Please input valid IP address"); return; }
            try { port = Convert.ToUInt16(findOptionValue("host", "8080", args)); }
            catch { Console.WriteLine("Please input valid port number (0-65535)"); return; }

            Server server = new Server(host, port);
            server.Start();
            return;
        }
    }
}
