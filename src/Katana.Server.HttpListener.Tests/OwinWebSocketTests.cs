﻿using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Katana.Server.HttpListenerWrapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;
using Katana.Server.DotNetWebSockets;

namespace Katana.Server.HttpListener.Tests
{
    using WebSocketFunc =
        Func
        <
            IDictionary<string, object>, // WebSocket environment
            Task // Complete
        >;

    using WebSocketSendAsync =
        Func
        <
            ArraySegment<byte> /* data */,
            int /* messageType */,
            bool /* endOfMessage */,
            CancellationToken /* cancel */,
            Task
        >;

    using WebSocketReceiveAsync =
        Func
        <
            ArraySegment<byte> /* data */,
            CancellationToken /* cancel */,
            Task
            <
                Tuple
                <
                    int /* messageType */,
                    bool /* endOfMessage */,
                    int? /* count */,
                    int? /* closeStatus */,
                    string /* closeStatusDescription */
                >
            >
        >;

    using WebSocketReceiveTuple =
        Tuple
        <
            int /* messageType */,
            bool /* endOfMessage */,
            int? /* count */,
            int? /* closeStatus */,
            string /* closeStatusDescription */
        >;

    using WebSocketCloseAsync =
        Func
        <
            int /* closeStatus */,
            string /* closeDescription */,
            CancellationToken /* cancel */,
            Task
        >;

    [TestClass]
    public class OwinWebSocketTests
    {
        private static readonly string[] HttpServerAddress = new string[] { "http://*:8080/BaseAddress/" };
        private const string WsClientAddress = "ws://localhost:8080/BaseAddress/";
        private static readonly string[] HttpsServerAddress = new string[] { "https://*:9090/BaseAddress/" };
        private const string WssClientAddress = "wss://localhost:9090/BaseAddress/";

        [TestMethod]
        public async Task EndToEnd_ConnectAndClose_Success()
        {
            OwinHttpListener listener = new OwinHttpListener(
                WebSocketWrapperExtensions.HttpListenerMiddleware(env =>
                {
                    string support = (string)env["websocket.Support"];
                    Assert.IsTrue(support == "WebSocketFunc");
                    WebSocketFunc body = async (wsEnv) =>
                    {
                        WebSocketSendAsync sendAsync = wsEnv.Get<WebSocketSendAsync>("websocket.SendAsyncFunc");
                        WebSocketReceiveAsync receiveAsync = wsEnv.Get<WebSocketReceiveAsync>("websocket.ReceiveAsyncFunc");
                        WebSocketCloseAsync closeAsync = wsEnv.Get<WebSocketCloseAsync>("websocket.CloseAsyncFunc");

                        ArraySegment<byte> buffer = new ArraySegment<byte>(new byte[10]);
                        await receiveAsync(buffer, CancellationToken.None);
                        await closeAsync((int)WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    };

                    env["owin.ResponseStatusCode"] = 101;
                    env["websocket.Func"] = body;

                    return TaskHelpers.Completed();
                }),
                HttpServerAddress);

            using (listener)
            {
                listener.Start();
                using (ClientWebSocket client = new ClientWebSocket())
                {
                    await client.ConnectAsync(new Uri(WsClientAddress), CancellationToken.None);

                    await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    WebSocketReceiveResult readResult = await client.ReceiveAsync(new ArraySegment<byte>(new byte[10]), CancellationToken.None);

                    Assert.AreEqual(WebSocketCloseStatus.NormalClosure, readResult.CloseStatus);
                    Assert.AreEqual("Closing", readResult.CloseStatusDescription);
                    Assert.AreEqual(0, readResult.Count);
                    Assert.IsTrue(readResult.EndOfMessage);
                    Assert.AreEqual(WebSocketMessageType.Close, readResult.MessageType);
                }
            }
        }

        [TestMethod]
        public async Task EndToEnd_EchoData_Success()
        {
            OwinHttpListener listener = new OwinHttpListener(
                WebSocketWrapperExtensions.HttpListenerMiddleware(env =>
                {
                    WebSocketFunc body =
                        async (wsEnv) =>
                        {
                            WebSocketSendAsync sendAsync = wsEnv.Get<WebSocketSendAsync>("websocket.SendAsyncFunc");
                            WebSocketReceiveAsync receiveAsync = wsEnv.Get<WebSocketReceiveAsync>("websocket.ReceiveAsyncFunc");
                            WebSocketCloseAsync closeAsync = wsEnv.Get<WebSocketCloseAsync>("websocket.CloseAsyncFunc");

                            ArraySegment<byte> buffer = new ArraySegment<byte>(new byte[100]);
                            var serverReceive = await receiveAsync(buffer, CancellationToken.None);
                            await sendAsync(new ArraySegment<byte>(buffer.Array, 0, serverReceive.Item3.Value),
                                serverReceive.Item1, serverReceive.Item2, CancellationToken.None);
                            await closeAsync((int)WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        };

                    env["owin.ResponseStatusCode"] = 101;
                    env["websocket.Func"] = body;

                    return TaskHelpers.Completed();
                }),
                HttpServerAddress);

            using (listener)
            {
                listener.Start();
                using (ClientWebSocket client = new ClientWebSocket())
                {
                    await client.ConnectAsync(new Uri(WsClientAddress), CancellationToken.None);

                    byte[] sendBody = Encoding.UTF8.GetBytes("Hello World");
                    await client.SendAsync(new ArraySegment<byte>(sendBody), WebSocketMessageType.Text, true, CancellationToken.None);
                    byte[] receiveBody = new byte[100];
                    WebSocketReceiveResult readResult = await client.ReceiveAsync(new ArraySegment<byte>(receiveBody), CancellationToken.None);

                    Assert.AreEqual(WebSocketMessageType.Text, readResult.MessageType);
                    Assert.IsTrue(readResult.EndOfMessage);
                    Assert.AreEqual(sendBody.Length, readResult.Count);
                    Assert.AreEqual("Hello World", Encoding.UTF8.GetString(receiveBody, 0, readResult.Count));
                }
            }
        }

        [TestMethod]
        public async Task SubProtocol_SelectLastSubProtocol_Success()
        {
            OwinHttpListener listener = new OwinHttpListener(
                WebSocketWrapperExtensions.HttpListenerMiddleware(env =>
                {
                    WebSocketFunc body =
                        async (wsEnv) =>
                        {
                            WebSocketSendAsync sendAsync = wsEnv.Get<WebSocketSendAsync>("websocket.SendAsyncFunc");
                            WebSocketReceiveAsync receiveAsync = wsEnv.Get<WebSocketReceiveAsync>("websocket.ReceiveAsyncFunc");
                            WebSocketCloseAsync closeAsync = wsEnv.Get<WebSocketCloseAsync>("websocket.CloseAsyncFunc");

                            ArraySegment<byte> buffer = new ArraySegment<byte>(new byte[100]);
                            var serverReceive = await receiveAsync(buffer, CancellationToken.None);
                            // Assume close received
                            await closeAsync((int)WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        };

                    var requestHeaders = env.Get<IDictionary<string, string[]>>("owin.RequestHeaders");
                    var responseHeaders = env.Get<IDictionary<string, string[]>>("owin.ResponseHeaders");

                    // Select the last sub-protocol from the client.
                    string subProtocol = requestHeaders["Sec-WebSocket-Protocol"].Last().Split(',').Last().Trim();

                    responseHeaders["Sec-WebSocket-Protocol"] = new string[] { subProtocol };

                    env["owin.ResponseStatusCode"] = 101;
                    env["websocket.Func"] = body;

                    return TaskHelpers.Completed();
                }),
                HttpServerAddress);

            using (listener)
            {
                listener.Start();
                using (ClientWebSocket client = new ClientWebSocket())
                {
                    client.Options.AddSubProtocol("protocol1");
                    client.Options.AddSubProtocol("protocol2");

                    await client.ConnectAsync(new Uri(WsClientAddress), CancellationToken.None);

                    await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    byte[] receiveBody = new byte[100];
                    WebSocketReceiveResult readResult = await client.ReceiveAsync(new ArraySegment<byte>(receiveBody), CancellationToken.None);
                    Assert.AreEqual(WebSocketMessageType.Close, readResult.MessageType);
                    Assert.AreEqual("protocol2", client.SubProtocol);
                }
            }
        }
    }
}