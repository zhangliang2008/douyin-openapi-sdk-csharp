using System;
using System.IO;
using Newtonsoft.Json;

namespace DouyinOpenApi
{
    /// <summary>
    /// 上传文件描述，对应 Go SDK 的 <c>util.FileField</c>。
    /// <para>
    /// 仅用于 <see cref="ParamLocation.Multipart"/> 类接口（视频上传、图片上传、素材上传等）。
    /// </para>
    /// </summary>
    public sealed class FormFile
    {
        /// <summary>
        /// 文件名，会写入 multipart 的 <c>filename=</c>。抖音接口通常按扩展名判断文件类型，务必带正确后缀。
        /// </summary>
        [JsonProperty("filename")]
        public string FileName { get; set; }

        /// <summary>
        /// 文件 MIME 类型，如 <c>video/mp4</c>、<c>image/jpeg</c>。
        /// </summary>
        [JsonProperty("content-type")]
        public string ContentType { get; set; }

        /// <summary>
        /// 文件内容流。调用方负责流的生命周期：SDK 只读取不关闭。
        /// </summary>
        [JsonIgnore]
        public Stream Content { get; set; }

        /// <summary>
        /// 用内存字节数组构造上传文件。
        /// </summary>
        /// <param name="bytes">文件二进制内容。</param>
        /// <param name="fileName">文件名（含扩展名）。</param>
        /// <param name="contentType">MIME 类型；为空时按扩展名推断。</param>
        public static FormFile FromBytes(byte[] bytes, string fileName, string contentType = null)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            return new FormFile
            {
                FileName = fileName,
                ContentType = contentType ?? GuessContentType(fileName),
                Content = new MemoryStream(bytes, writable: false)
            };
        }

        /// <summary>
        /// 用本地文件构造上传文件。注意：大文件（如分片上传的视频）只是打开文件流，不会整体读入内存。
        /// </summary>
        /// <param name="filePath">本地文件绝对或相对路径。</param>
        /// <param name="contentType">MIME 类型；为空时按扩展名推断。</param>
        public static FormFile FromFile(string filePath, string contentType = null)
        {
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentNullException(nameof(filePath));
            // FileShare.Read 允许其它进程同时读取，避免被占用导致上传失败
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new FormFile
            {
                FileName = Path.GetFileName(filePath),
                ContentType = contentType ?? GuessContentType(filePath),
                Content = stream
            };
        }

        /// <summary>
        /// 用已打开的流构造上传文件。
        /// </summary>
        /// <param name="stream">文件内容流。</param>
        /// <param name="fileName">文件名（含扩展名）。</param>
        /// <param name="contentType">MIME 类型；为空时按扩展名推断。</param>
        public static FormFile FromStream(Stream stream, string fileName, string contentType = null)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            return new FormFile
            {
                FileName = fileName,
                ContentType = contentType ?? GuessContentType(fileName),
                Content = stream
            };
        }

        /// <summary>
        /// 按扩展名猜一个 MIME 类型。抖音上传接口对 Content-Type 不严格，猜错的影响通常只是服务端类型校验提示，
        /// 因此这里只覆盖最常见的几种，其余回退到 <c>application/octet-stream</c>。
        /// </summary>
        private static string GuessContentType(string fileName)
        {
            var ext = string.IsNullOrEmpty(fileName) ? string.Empty : Path.GetExtension(fileName).ToLowerInvariant();
            switch (ext)
            {
                case ".mp4": return "video/mp4";
                case ".mov": return "video/quicktime";
                case ".avi": return "video/x-msvideo";
                case ".flv": return "video/x-flv";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".png": return "image/png";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".bmp": return "image/bmp";
                case ".txt": return "text/plain";
                case ".json": return "application/json";
                default: return "application/octet-stream";
            }
        }
    }
}
