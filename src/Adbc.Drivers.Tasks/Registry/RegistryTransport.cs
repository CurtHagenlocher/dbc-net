using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Adbc.Drivers.Build.Registry
{
    /// <summary>
    /// Fetches registry indexes and driver archives. Abstracted so tests can serve a
    /// fixture registry without a network.
    /// </summary>
    internal interface IRegistryTransport
    {
        /// <summary>Opens the resource for reading. The caller disposes the stream.</summary>
        Stream OpenRead(Uri uri);

        /// <summary>Reads a small text resource such as an index.</summary>
        string ReadAllText(Uri uri, long maxBytes);
    }

    internal sealed class RegistryTransportException : Exception
    {
        public RegistryTransportException(string message)
            : base(message)
        {
        }

        public RegistryTransportException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>
    /// The default transport: HTTPS (plus HTTP to loopback, and <c>file://</c>, for
    /// tests and private mirrors).
    /// </summary>
    /// <remarks>
    /// Deliberately does not reproduce <c>dbc</c>'s persistent <c>mid</c>/<c>uid</c>
    /// query parameters. A build tool has no business attaching stable machine or user
    /// identifiers to package requests, so only an ordinary product User-Agent is sent.
    /// </remarks>
    internal sealed class DefaultRegistryTransport : IRegistryTransport, IDisposable
    {
        private static readonly string UserAgent = BuildUserAgent();

        private readonly HttpClient _client;
        private readonly bool _allowInsecureHttp;

        public DefaultRegistryTransport(TimeSpan timeout, bool allowInsecureHttp = false)
        {
            _allowInsecureHttp = allowInsecureHttp;

#if NET472
            // .NET Framework does not negotiate TLS 1.2+ by default in every host.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#endif

            _client = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
            })
            {
                Timeout = timeout,
            };

            _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        }

        public Stream OpenRead(Uri uri)
        {
            if (uri is null) throw new ArgumentNullException(nameof(uri));

            Validate(uri);

            if (uri.IsFile)
            {
                string path = uri.LocalPath;
                if (!File.Exists(path))
                {
                    throw new RegistryTransportException($"'{path}' does not exist.");
                }

                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024);
            }

            try
            {
                // Task.Run keeps the synchronous wait off whatever context the MSBuild
                // task was invoked on.
                HttpResponseMessage response = Task.Run(() =>
                    _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead)).GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    int status = (int)response.StatusCode;
                    string reason = response.ReasonPhrase ?? "no reason given";
                    response.Dispose();
                    throw new RegistryTransportException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "GET {0} returned HTTP {1} ({2}).",
                            Redact(uri),
                            status,
                            reason));
                }

                return new ResponseStream(response);
            }
            catch (RegistryTransportException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                throw new RegistryTransportException($"GET {Redact(uri)} failed: {ex.Message}", ex);
            }
        }

        public string ReadAllText(Uri uri, long maxBytes)
        {
            using (Stream stream = OpenRead(uri))
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[64 * 1024];
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (buffer.Length + read > maxBytes)
                    {
                        throw new RegistryTransportException(
                            $"{Redact(uri)} is larger than the {maxBytes} byte limit for a registry index.");
                    }

                    buffer.Write(chunk, 0, read);
                }

                byte[] bytes = buffer.ToArray();
                return StripByteOrderMark(Encoding.UTF8.GetString(bytes));
            }
        }

        public void Dispose() => _client.Dispose();

        /// <summary>
        /// Strips credentials and query strings from a URL before it appears in build
        /// output, since either can carry a token.
        /// </summary>
        internal static string Redact(Uri uri)
        {
            if (uri.IsFile)
            {
                return uri.LocalPath;
            }

            UriBuilder builder = new UriBuilder(uri)
            {
                UserName = string.Empty,
                Password = string.Empty,
            };

            if (!string.IsNullOrEmpty(builder.Query))
            {
                builder.Query = "(redacted)";
            }

            return builder.Uri.AbsoluteUri;
        }

        private void Validate(Uri uri)
        {
            if (uri.IsFile)
            {
                return;
            }

            if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                if (_allowInsecureHttp || IsLoopback(uri))
                {
                    return;
                }

                throw new RegistryTransportException(
                    $"Refusing to use the plaintext URL {Redact(uri)}. Use https, or a loopback address for a local test registry.");
            }

            throw new RegistryTransportException($"The URL scheme '{uri.Scheme}' is not supported.");
        }

        private static bool IsLoopback(Uri uri)
        {
            if (uri.IsLoopback)
            {
                return true;
            }

            return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        }

        private const char ByteOrderMark = '\uFEFF';

        private static string StripByteOrderMark(string text) =>
            text.Length > 0 && text[0] == ByteOrderMark ? text.Substring(1) : text;

        private static string BuildUserAgent()
        {
            string version = typeof(DefaultRegistryTransport).GetTypeInfo().Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "0.0.0";

            // Build metadata can contain a source revision id; keep the UA short.
            int plus = version.IndexOf('+');
            if (plus > 0)
            {
                version = version.Substring(0, plus);
            }

            return $"Adbc.Drivers.Build/{version} (+https://github.com/CurtHagenlocher/dbc-net)";
        }

        /// <summary>Keeps the HTTP response alive for as long as its content stream.</summary>
        private sealed class ResponseStream : Stream
        {
            private readonly HttpResponseMessage _response;
            private readonly Stream _inner;

            public ResponseStream(HttpResponseMessage response)
            {
                _response = response;
                _inner = Task.Run(() => response.Content.ReadAsStreamAsync()).GetAwaiter().GetResult();
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                    _response.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
