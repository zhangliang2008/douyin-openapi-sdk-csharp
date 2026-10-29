using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace DouyinOpenApi.Internal
{
    /// <summary>
    /// 包装上传流，保证 HTTP 请求结束后【不关闭】调用方传入的流。
    /// <para>
    /// 默认的 <see cref="StreamContent"/> 会随请求对象一起 Dispose，从而把用户的
    /// <see cref="FileStream"/> 关掉；调用方若想复用同一个流（例如分片上传、失败重试）
    /// 就会踩到 "Cannot access a closed stream"。这里只转发读取，不接管生命周期。
    /// </para>
    /// </summary>
    internal sealed class NonClosingStreamContent : HttpContent
    {
        private readonly Stream _stream;

        /// <summary>
        /// 用给定流与 MIME 类型构造内容体。
        /// </summary>
        /// <param name="stream">待上传的流。</param>
        /// <param name="contentType">MIME 类型。</param>
        internal NonClosingStreamContent(Stream stream, string contentType)
        {
            _stream = stream;
            Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrEmpty(contentType)
                ? "application/octet-stream"
                : contentType);
        }

        /// <summary>把流内容拷贝到请求体。</summary>
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
        {
            return _stream.CopyToAsync(stream);
        }

        /// <summary>
        /// 可 Seek 时给出准确的 Content-Length，避免走 chunked 传输（部分网关对 chunked 支持不佳）。
        /// </summary>
        protected override bool TryComputeLength(out long length)
        {
            if (_stream.CanSeek)
            {
                length = _stream.Length - _stream.Position;
                return true;
            }

            length = -1;
            return false;
        }
    }
}
