/*
 * Copyright (c) 2013 devunt (original python code: warp.py)
 * https://github.com/devunt/warp
 * 
 * Copyright (c) 2013 SaschaNaz
 * 
 * Permission is hereby granted, free of charge, to any person
 * obtaining a copy of this software and associated documentation
 * files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use,
 * copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following
 * conditions:
 * 
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 * OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 * OTHER DEALINGS IN THE SOFTWARE.
 * 
 * HTTP Tunneling is implemented as:
 * http://www.web-cache.com/Writings/Internet-Drafts/draft-luotonen-web-proxy-tunneling-01.txt
 */

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

namespace Lancer
{
    class Server
    {
        Regex RContentLength = new Regex("\r\nContent-Length: ([0-9]+)\r\n");
        Regex RConnection = new Regex("\r\nConnection: (.+)\r\n");

        IPAddress hostname;
        Int32 port;
        Random r = new Random();
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
                socket.Bind(new System.Net.IPEndPoint(hostname, port));//[::1]
            }
            catch
            {
                Console.WriteLine(String.Format("!!! Failed to bind server at [{0}:{1}]", hostname, port)); 
                return;
            }
            Console.WriteLine(String.Format("Server bound at [{0}:{1}].", hostname, port)); 
            socket.Listen(128);

            while (true)
            {
                Socket acceptedSocket = socket.Accept();
                acceptedSocket.ReceiveTimeout = acceptedSocket.SendTimeout = 60000;
                new Task(async delegate()
                {
                    try
                    {
                        await Request(acceptedSocket);
                    }
                    catch (SocketException e)
                    {
                        Console.WriteLine(e.Message);
                    }
                }).Start();
            }
        }

        String receiveHttpMessage(Socket socket, MemoryStream stream)
        {
            List<Byte> headerbytes = new List<Byte>();
            UInt16 endcounter = 0;
            while (endcounter != 4)
            {
                Byte[] buffer = new Byte[1024];
                //List<ArraySegment<Byte>> buffer = new List<ArraySegment<Byte>>();
                Int32 received = socket.Receive(buffer);
                for (Int32 i = 0; i < received; i++)
                {
                    if (endcounter != 4)
                    {
                        switch (buffer[i])
                        {
                            case 0x0D:
                                if (endcounter == 0 || endcounter == 2)
                                    endcounter++;
                                break;
                            case 0x0A:
                                if (endcounter == 1 || endcounter == 3)
                                    endcounter++;
                                break;
                            default:
                                endcounter = 0;
                                break;
                        }
                        headerbytes.Add(buffer[i]);
                    }
                    else
                        stream.WriteByte(buffer[i]);
                }
            }
            String headerstr = Encoding.UTF8.GetString(headerbytes.ToArray());
            Match lengthm = RContentLength.Match(headerstr);
            if (lengthm.Groups.Count > 0 && lengthm.Groups[0].Value.Length != 0)
            {
                Int32 length = Convert.ToInt32(lengthm.Groups[0].Value.Substring(18).TrimEnd('\r', '\n'));
                while (length != stream.Length)
                {
                    Byte[] buffer = new Byte[1024];
                    Int32 received = socket.Receive(buffer);
                    stream.Write(buffer, 0, received);
                }
            }
            return headerstr;
        }

        async Task makeDataTunnel(Socket localSocket, Socket remoteSocket, Int32 timeout)
        {
            DateTime lastRespondedTime = DateTime.Now;
            //Parallel.Invoke(
            //async delegate()
            //{
            while (true)
            {
                if (localSocket.Available > 0 || remoteSocket.Available > 0)
                {
                    lastRespondedTime = DateTime.Now;
                    Byte[] buffer = new Byte[1024];
                    if (localSocket.Available > 0)
                    {
                        Int32 received = localSocket.Receive(buffer, 1024, SocketFlags.None);
                        remoteSocket.Send(buffer, 0, received, SocketFlags.None);
                    }
                    if (remoteSocket.Available > 0)
                    {
                        Int32 received = remoteSocket.Receive(buffer, 1024, SocketFlags.None);
                        localSocket.Send(buffer, 0, received, SocketFlags.None);
                    }
                }
                else
                {
                    TimeSpan span = (DateTime.Now - lastRespondedTime);
                    if (span.TotalMilliseconds < timeout)
                        await Task.Delay(200);
                    else
                        break;
                }
            }
            //});
        }

        async Task writeStreamToSocket(Socket socket, MemoryStream stream)
        {
            stream.Position = 0;
            while (true)
            {
                Byte[] buffer = new Byte[1024];
                Int32 read = await stream.ReadAsync(buffer, 0, 1024);
                socket.Send(buffer, read, SocketFlags.None);
                if (read == 0)
                    break;
            }
        }

        public async Task Request(Socket socket)
        {
            Int32 connectionId = r.Next(0, 65535);
            // (socket.RemoteEndPoint as IPEndPoint).Address);
            MemoryStream contentStream = new MemoryStream();
            String headerstr = receiveHttpMessage(socket, contentStream);

            String[] requests = headerstr.TrimEnd('\r', '\n').Split(new String[] { "\r\n" }, StringSplitOptions.None);

            String[] heads = requests[0].Split(' ');
            Uri targeturi;
            if (heads[1].Contains("://"))
                targeturi = new Uri(heads[1]);
            else
                targeturi = new Uri("protocol://" + heads[1]);//just for getting local path. currently no support for HTTPS. HTTP tunneling required.
            Socket requestSocket;
            if (heads[0] == "CONNECT")
            {
                socket.Send(Encoding.UTF8.GetBytes("HTTP/1.0 200 Connection established\r\n\r\n"));
                requestSocket = new Socket(SocketType.Stream, ProtocolType.IP);
                requestSocket.NoDelay = true;
                requestSocket.Connect(targeturi.Host, targeturi.Port);
                Console.WriteLine(String.Format("New: Encrypted\tConnection: {0}\r\n\t{1}", connectionId, heads[1]));
            }
            else
            {
                String proxyHost = String.Empty;
                List<String> sRequests = new List<String>();
                for (Int32 i = 1; i < requests.Length; i++)
                {
                    if (requests[i].Contains("Host: "))
                        proxyHost = requests[i].Substring(6);
                    else if (!requests[i].Contains("Proxy-Connection"))
                        sRequests.Add(requests[i]);
                }

                Match connectionm = RConnection.Match(headerstr);
                if (connectionm.Groups.Count == 0)
                    sRequests.Add(String.Format("Connection: {0}", connectionm.Groups[0].Value));
                else
                    sRequests.Add("Connection: close");

                String path = targeturi.PathAndQuery;

                Console.WriteLine(String.Format("New: Normal\tConnection: {0}\r\n\t{1}", connectionId, requests[0]));

                String newHead = String.Join(" ", heads[0], path, heads[2]);

                requestSocket = new Socket(SocketType.Stream, ProtocolType.IP);
                requestSocket.NoDelay = true;
                requestSocket.ReceiveTimeout = requestSocket.SendTimeout = 60000;
                if (targeturi.Host.Length > 0)
                    requestSocket.Connect(targeturi.Host, targeturi.Port);
                else
                    requestSocket.Connect(proxyHost, 80);

                requestSocket.Send(Encoding.UTF8.GetBytes(newHead + "\r\nHost: "));
                {
                    String remaining = proxyHost;
                    Int32 i = 1;
                    while (remaining.Length > 0)
                    {
                        await Task.Delay(r.Next(2, 4));
                        if (remaining.Length > i)
                        {
                            requestSocket.Send(Encoding.UTF8.GetBytes(remaining.Substring(0, i)));
                            remaining = remaining.Substring(i);
                        }
                        else
                        {
                            requestSocket.Send(Encoding.UTF8.GetBytes(remaining));
                            remaining = String.Empty;
                        }
                        i = r.Next(2, 5);
                    }
                }
                requestSocket.Send(Encoding.UTF8.GetBytes("\r\n" + String.Join("\r\n", sRequests) + "\r\n\r\n"));
                if (contentStream.Length > 0)
                    await writeStreamToSocket(requestSocket, contentStream);
                contentStream.Dispose();

                //while (true)
                //{
                //    Byte[] buffer = new Byte[1024];
                //    Int32 received = requestSocket.Receive(buffer);
                //    String str = Encoding.UTF8.GetString(buffer);
                //    socket.Send(buffer, received, SocketFlags.None);
                //    if (received == 0)
                //        break;
                //}
                //requestSocket.Close();
                //requestSocket.Dispose();

                //Console.WriteLine("Task done");
                //socket.Close();
                //socket.Dispose();
            }
            try
            {
                await makeDataTunnel(socket, requestSocket, 60000);
                Console.WriteLine(String.Format("Closed: Type 1\tConnection: {0}", connectionId));
            }
            catch (SocketException)
            {
                Console.WriteLine(String.Format("Closed: Type 2\tConnection: {0}", connectionId));
            }
            finally
            {
                socket.Close();
                requestSocket.Close();
                socket.Dispose();
                requestSocket.Dispose();
            }
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

        static Boolean findOption(String optionname, String[] args)
        {
            List<String> argsList = args.ToList();
            Int32 foundIndex = argsList.FindIndex((String s) => { if ((s[0] == '-' || s[0] == '/') && s.Substring(1) == optionname) return true; else return false; });
            if (foundIndex != -1) return true;
            else return false;
        }

        static void Main(string[] args)
        {
            if (findOption("?", args))
            {
                Console.WriteLine("USAGE:");
                Console.WriteLine("\t lancer [/host IPADDRESS] [/port PORTNUMBER]");
            }

            IPAddress host;
            UInt16 port;
            try { host = IPAddress.Parse(findOptionValue("host", "127.0.0.1", args)); }
            catch { Console.WriteLine("Please input valid IP address"); return; }  
            try { port = Convert.ToUInt16(findOptionValue("port", "8080", args)); }
            catch { Console.WriteLine("Please input valid port number (0-65535)"); return; }


            Console.WriteLine("(c)SaschaNaz");
            Console.WriteLine("Lancer, the ported version of warp.py");
            Console.WriteLine("");
            Server server = new Server(host, port);
            server.Start();
            return;
        }
    }
}
