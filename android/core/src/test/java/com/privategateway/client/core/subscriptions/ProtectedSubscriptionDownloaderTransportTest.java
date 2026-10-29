package com.privategateway.client.core.subscriptions;

import com.privategateway.client.core.xray.SubscriptionProxy;
import kotlin.Unit;
import org.junit.jupiter.api.Test;
import javax.net.SocketFactory;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;
import static org.junit.jupiter.api.Assertions.*;

/** Настоящий scoped OkHttp клиента приложения, без сети вне петли: .invalid нельзя найти напрямую. */
final class ProtectedSubscriptionDownloaderTransportTest {
    private ProtectedSubscriptionDownloader downloader(int port) {
        return new ProtectedSubscriptionDownloader(
                new SubscriptionProxy(port, "test-user", "test-password"),
                SocketFactory.getDefault(), new SubscriptionRequest(), () -> Unit.INSTANCE);
    }

    @Test void httpUses407ScopedAuthenticationWithoutDirectDns() throws Exception {
        try (ProxyStub proxy = new ProxyStub()) {
            SubscriptionContent body = downloader(proxy.port()).download(URI.create("http://subscription.invalid/sub/fake-token"));
            assertEquals("test-subscription", body.getText());
            assertTrue(proxy.requests().stream().anyMatch(x -> x.startsWith("GET http://subscription.invalid/sub/fake-token HTTP/1.1")));
            assertTrue(proxy.requests().stream().anyMatch(proxy::authenticated));
        }
    }

    @Test void httpsConnectAuthenticatesWithoutSendingThePathAndDoesNotDisableTls() throws Exception {
        try (ProxyStub proxy = new ProxyStub()) {
            // После 200 CONNECT сервер закрывает сокет. Стандартный TLS обязан отказать; клиент не читает открытый HTTP
            // вместо HTTPS и не использует доверие «любому сертификату» ради прокси.
            SubscriptionDownloadException failure = assertThrows(SubscriptionDownloadException.class,
                    () -> downloader(proxy.port()).download(URI.create("https://subscription.invalid/sub/fake-token")));
            assertEquals(DownloadFailure.Tls, failure.getFailure());
            String connect = proxy.requests().stream().filter(x -> x.startsWith("CONNECT ")).findFirst().orElseThrow();
            assertTrue(connect.startsWith("CONNECT subscription.invalid:443 HTTP/1.1"));
            assertTrue(proxy.authenticated(connect), "CONNECT должен получить credential через per-client proxyAuthenticator");
            assertFalse(connect.contains("/sub/fake-token"), "путь HTTPS не идет в незашифрованном CONNECT");
        }
    }

    @Test void aDeadProxyCannotFallBackToTheDirectLocalOrigin() throws Exception {
        try (ProxyStub origin = new ProxyStub()) {
            final int port;
            try (ServerSocket reserve = new ServerSocket(0, 8, InetAddress.getByName("127.0.0.1"))) { port = reserve.getLocalPort(); }
            assertThrows(SubscriptionDownloadException.class,
                    () -> downloader(port).download(URI.create("http://127.0.0.1:" + origin.port() + "/sub/fake-token")));
            assertTrue(origin.requests().isEmpty(), "прямой запрос адреса с токеном запрещен даже для локального хоста");
        }
    }

    @Test void anHttpsOriginCannotAskForTheLocalProxyPasswordInsideItsTunnel() throws Exception {
        ProtectedSubscriptionDownloader source = downloader(19000);
        var field = ProtectedSubscriptionDownloader.class.getDeclaredField("client");
        field.setAccessible(true);
        okhttp3.OkHttpClient client = (okhttp3.OkHttpClient) field.get(source);
        okhttp3.Address address = new okhttp3.Address("subscription.invalid", 443, client.dns(), client.socketFactory(),
                client.sslSocketFactory(), client.hostnameVerifier(), client.certificatePinner(), client.proxyAuthenticator(),
                client.proxy(), client.protocols(), client.connectionSpecs(), client.proxySelector());
        okhttp3.Route route = new okhttp3.Route(address, client.proxy(), new InetSocketAddress("127.0.0.1", 19000));
        okhttp3.Response forged = new okhttp3.Response.Builder()
                .request(new okhttp3.Request.Builder().url("https://subscription.invalid/sub/fake-token").build())
                .protocol(okhttp3.Protocol.HTTP_1_1).code(407).message("Proxy Authentication Required")
                .header("Proxy-Authenticate", "Basic realm=\"pretending-to-be-the-proxy\"").build();
        assertNull(client.proxyAuthenticator().authenticate(route, forged), "407 от HTTPS origin не должен получить пароль прокси");
    }

    @Test void cancellationClosesAnAuthenticatedCallWaitingForResponseHeaders() throws Exception {
        try (ProxyStub proxy = new ProxyStub(true)) {
            SubscriptionRequest request = new SubscriptionRequest();
            ProtectedSubscriptionDownloader source = new ProtectedSubscriptionDownloader(
                    new SubscriptionProxy(proxy.port(), "test-user", "test-password"),
                    SocketFactory.getDefault(), request, () -> Unit.INSTANCE);
            ExecutorService worker = Executors.newSingleThreadExecutor();
            try {
                Future<SubscriptionContent> result = worker.submit(
                        () -> source.download(URI.create("http://subscription.invalid/sub/fake-token")));
                assertTrue(proxy.authenticatedRequest.await(5, TimeUnit.SECONDS), "fixture must reach authenticated GET");
                request.cancel();
                ExecutionException cancelled = assertThrows(ExecutionException.class, () -> result.get(2, TimeUnit.SECONDS));
                assertInstanceOf(SubscriptionDownloadException.class, cancelled.getCause());
                assertTrue(proxy.clientClosed.await(2, TimeUnit.SECONDS), "request.onCancel must cancel the real OkHttp call/socket, not wait for 20s timeout");
            } finally {
                worker.shutdownNow();
            }
        }
    }

    private static final class ProxyStub implements AutoCloseable {
        private final ServerSocket listener;
        private final List<String> requests = Collections.synchronizedList(new ArrayList<>());
        private final String credential = "Basic " + Base64.getEncoder().encodeToString("test-user:test-password".getBytes(StandardCharsets.ISO_8859_1));
        private volatile IOException failure;
        private final boolean holdAfterAuthentication;
        private final CountDownLatch authenticatedRequest = new CountDownLatch(1);
        private final CountDownLatch clientClosed = new CountDownLatch(1);

        ProxyStub() throws IOException { this(false); }
        ProxyStub(boolean holdAfterAuthentication) throws IOException {
            this.holdAfterAuthentication = holdAfterAuthentication;
            listener = new ServerSocket(0, 8, InetAddress.getByName("127.0.0.1"));
            Thread worker = new Thread(() -> {
                while (!listener.isClosed()) {
                    try (Socket socket = listener.accept()) {
                        socket.setSoTimeout(3000);
                        String request = readHeaders(socket.getInputStream());
                        requests.add(request);
                        if (holdAfterAuthentication && authenticated(request) && request.startsWith("GET ")) {
                            authenticatedRequest.countDown();
                            try {
                                if (socket.getInputStream().read() == -1) clientClosed.countDown();
                            } catch (SocketException closed) {
                                clientClosed.countDown();
                            }
                            continue;
                        }
                        String response;
                        if (!authenticated(request)) {
                            response = "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"proxy\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                        } else if (request.startsWith("CONNECT ")) {
                            response = "HTTP/1.1 200 Connection Established\r\n\r\n";
                        } else {
                            String body = "test-subscription";
                            response = "HTTP/1.1 200 OK\r\nContent-Length: " + body.length() + "\r\nConnection: close\r\n\r\n" + body;
                        }
                        socket.getOutputStream().write(response.getBytes(StandardCharsets.ISO_8859_1));
                        socket.getOutputStream().flush();
                    } catch (IOException e) {
                        if (!listener.isClosed()) { failure = e; return; }
                    }
                }
            }, "subscription-test-proxy");
            worker.setDaemon(true);
            worker.start();
        }

        int port() { return listener.getLocalPort(); }
        List<String> requests() {
            if (failure != null) throw new AssertionError("proxy fixture failed", failure);
            synchronized (requests) { return List.copyOf(requests); }
        }
        boolean authenticated(String request) {
            return request.lines().anyMatch(line -> {
                int separator = line.indexOf(':');
                return separator > 0 && line.substring(0, separator).equalsIgnoreCase("Proxy-Authorization")
                        && line.substring(separator + 1).trim().equals(credential);
            });
        }
        @Override public void close() throws IOException { listener.close(); }

        private static String readHeaders(InputStream input) throws IOException {
            ByteArrayOutputStream bytes = new ByteArrayOutputStream();
            while (bytes.size() < 16_384) {
                int value = input.read();
                if (value < 0) break;
                bytes.write(value);
                byte[] b = bytes.toByteArray();
                int n = b.length;
                if (n >= 4 && b[n-4] == 13 && b[n-3] == 10 && b[n-2] == 13 && b[n-1] == 10) return bytes.toString(StandardCharsets.ISO_8859_1);
            }
            throw new EOFException("HTTP header incomplete");
        }
    }
}
