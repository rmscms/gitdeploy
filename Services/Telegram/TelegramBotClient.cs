using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Models;
using Newtonsoft.Json.Linq;

namespace GitDeployPro.Services.Telegram
{
    public sealed class TelegramBotClient : IDisposable
    {
        private readonly HttpClient _http = new()
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        public async Task<TelegramApiResult> GetMeAsync(string token, CancellationToken cancellationToken)
        {
            return await CallAsync(token, "getMe", null, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<TelegramIncomingUpdate>> GetUpdatesAsync(
            string token,
            long offset,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            var query = $"getUpdates?offset={offset}&timeout={timeoutSeconds}&allowed_updates=%5B%22message%22%2C%22callback_query%22%5D";
            var api = await CallAsync(token, query, null, cancellationToken).ConfigureAwait(false);
            if (!api.Ok || api.Result is not JArray array)
            {
                if (!api.Ok && !string.IsNullOrWhiteSpace(api.Description))
                {
                    throw new InvalidOperationException(api.Description);
                }

                return Array.Empty<TelegramIncomingUpdate>();
            }

            var list = new List<TelegramIncomingUpdate>(array.Count);
            foreach (var item in array.OfType<JObject>())
            {
                var parsed = ParseIncoming(item);
                if (parsed != null)
                {
                    list.Add(parsed);
                }
            }

            return list;
        }

        public async Task<long> SendMessageAsync(
            string token,
            long chatId,
            string text,
            CancellationToken cancellationToken,
            JToken? replyMarkup = null,
            string? parseMode = null)
        {
            var payload = new JObject
            {
                ["chat_id"] = chatId,
                ["text"] = text ?? string.Empty
            };
            if (!string.IsNullOrWhiteSpace(parseMode))
            {
                payload["parse_mode"] = parseMode;
            }

            if (replyMarkup != null)
            {
                payload["reply_markup"] = replyMarkup;
            }

            var api = await CallAsync(token, "sendMessage", payload, cancellationToken).ConfigureAwait(false);
            if (!api.Ok)
            {
                throw new InvalidOperationException(api.Description);
            }

            return api.Result is JObject msg ? msg.Value<long?>("message_id") ?? 0 : 0;
        }

        public async Task<bool> DeleteMessageAsync(
            string token,
            long chatId,
            long messageId,
            CancellationToken cancellationToken)
        {
            if (chatId == 0 || messageId == 0)
            {
                return false;
            }

            var payload = new JObject
            {
                ["chat_id"] = chatId,
                ["message_id"] = messageId
            };

            var api = await CallAsync(token, "deleteMessage", payload, cancellationToken).ConfigureAwait(false);
            return api.Ok;
        }

        public async Task AnswerCallbackQueryAsync(
            string token,
            string callbackQueryId,
            string? text,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(callbackQueryId))
            {
                return;
            }

            var payload = new JObject
            {
                ["callback_query_id"] = callbackQueryId
            };
            if (!string.IsNullOrWhiteSpace(text))
            {
                payload["text"] = text;
            }

            await CallAsync(token, "answerCallbackQuery", payload, cancellationToken).ConfigureAwait(false);
        }

        public async Task SendPhotoAsync(
            string token,
            long chatId,
            string photoPath,
            string? caption,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(photoPath) || !File.Exists(photoPath))
            {
                throw new FileNotFoundException("Photo file was not found.", photoPath);
            }

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(chatId.ToString()), "chat_id");
            if (!string.IsNullOrWhiteSpace(caption))
            {
                form.Add(new StringContent(caption), "caption");
            }

            await using var stream = File.OpenRead(photoPath);
            var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(GuessImageType(photoPath));
            form.Add(fileContent, "photo", Path.GetFileName(photoPath));

            using var request = new HttpRequestMessage(HttpMethod.Post, Api(token, "sendPhoto"))
            {
                Content = form
            };
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var api = ParseApi(json);
            if (!api.Ok)
            {
                throw new InvalidOperationException(api.Description);
            }
        }

        public async Task<string> DownloadFileAsync(
            string token,
            string fileId,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            var api = await CallAsync(token, "getFile", new JObject { ["file_id"] = fileId }, cancellationToken)
                .ConfigureAwait(false);
            if (!api.Ok || api.Result is not JObject fileObj)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(api.Description)
                    ? "Could not resolve Telegram file."
                    : api.Description);
            }

            var filePath = fileObj.Value<string>("file_path");
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new InvalidOperationException("Telegram file path was empty.");
            }

            var url = $"https://api.telegram.org/file/bot{token}/{filePath}";
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromSeconds(45));
            using var response = await _http.GetAsync(url, linked.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? TelegramPaths.RootFolder);
            await File.WriteAllBytesAsync(destinationPath, bytes, linked.Token).ConfigureAwait(false);
            return destinationPath;
        }

        public void Dispose() => _http.Dispose();

        private async Task<TelegramApiResult> CallAsync(
            string token,
            string methodAndQuery,
            JObject? jsonBody,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(
                jsonBody == null ? HttpMethod.Get : HttpMethod.Post,
                Api(token, methodAndQuery));
            if (jsonBody != null)
            {
                request.Content = new StringContent(jsonBody.ToString(), System.Text.Encoding.UTF8, "application/json");
            }

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseApi(json);
        }

        private static TelegramApiResult ParseApi(string json)
        {
            try
            {
                var obj = JObject.Parse(json);
                var ok = obj.Value<bool?>("ok") == true;
                var description = obj.Value<string>("description") ?? (ok ? "OK" : "Telegram API error.");
                return new TelegramApiResult(ok, description, obj["result"]);
            }
            catch (Exception ex)
            {
                return new TelegramApiResult(false, ex.Message, null);
            }
        }

        private static TelegramIncomingUpdate? ParseIncoming(JObject update)
        {
            if (update["callback_query"] is JObject callback)
            {
                var from = callback["from"] as JObject;
                var message = callback["message"] as JObject;
                var chat = message?["chat"] as JObject ?? callback["chat"] as JObject;
                var userName = from?.Value<string>("username")
                               ?? from?.Value<string>("first_name")
                               ?? string.Empty;

                return new TelegramIncomingUpdate
                {
                    UpdateId = update.Value<long?>("update_id") ?? 0,
                    MessageId = message?.Value<long?>("message_id") ?? 0,
                    ChatId = chat?.Value<long?>("id") ?? 0,
                    UserId = from?.Value<long?>("id") ?? 0,
                    UserName = userName,
                    IsCallback = true,
                    CallbackQueryId = callback.Value<string>("id") ?? string.Empty,
                    CallbackData = callback.Value<string>("data") ?? string.Empty
                };
            }

            var body = update["message"] as JObject;
            if (body == null)
            {
                return null;
            }

            var msgFrom = body["from"] as JObject;
            var msgChat = body["chat"] as JObject;
            var photoId = PickLargestPhotoId(body["photo"] as JArray);
            var text = body.Value<string>("text") ?? string.Empty;
            var caption = body.Value<string>("caption") ?? string.Empty;
            var name = msgFrom?.Value<string>("username")
                       ?? msgFrom?.Value<string>("first_name")
                       ?? string.Empty;

            return new TelegramIncomingUpdate
            {
                UpdateId = update.Value<long?>("update_id") ?? 0,
                MessageId = body.Value<long?>("message_id") ?? 0,
                ChatId = msgChat?.Value<long?>("id") ?? 0,
                UserId = msgFrom?.Value<long?>("id") ?? 0,
                UserName = name,
                Text = text,
                Caption = caption,
                PhotoFileId = photoId
            };
        }

        private static string PickLargestPhotoId(JArray? photos)
        {
            if (photos == null || photos.Count == 0)
            {
                return string.Empty;
            }

            JObject? best = null;
            var bestArea = -1;
            foreach (var item in photos.OfType<JObject>())
            {
                var area = (item.Value<int?>("width") ?? 0) * (item.Value<int?>("height") ?? 0);
                if (area >= bestArea)
                {
                    bestArea = area;
                    best = item;
                }
            }

            return best?.Value<string>("file_id") ?? string.Empty;
        }

        private static string GuessImageType(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/jpeg"
            };
        }

        private static string Api(string token, string methodAndQuery)
            => "https://api.telegram.org/bot" + token + "/" + methodAndQuery;

        public readonly record struct TelegramApiResult(bool Ok, string Description, JToken? Result);
    }
}
