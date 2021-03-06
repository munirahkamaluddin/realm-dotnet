﻿////////////////////////////////////////////////////////////////////////////
//
// Copyright 2016 Realm Inc.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Json;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Realms.Sync.Exceptions;

namespace Realms.Sync
{
    internal static class AuthenticationHelper
    {
        private const int ErrorContentTruncationLimit = 256 * 1024;
        private static readonly Lazy<HttpClient> _client = new Lazy<HttpClient>(() => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        
        private static readonly ConcurrentDictionary<string, Timer> _tokenRefreshTimers = new ConcurrentDictionary<string, Timer>();
        private static readonly DateTimeOffset _date_1970 = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly MediaTypeHeaderValue _applicationJsonUtf8MediaType = MediaTypeHeaderValue.Parse("application/json; charset=utf-8");
        private static readonly MediaTypeHeaderValue _applicationProblemJsonUtf8MediaType = MediaTypeHeaderValue.Parse("application/problem+json; charset=utf-8");

        private static readonly HashSet<HttpStatusCode> _connectivityStatusCodes = new HashSet<HttpStatusCode>
        {
            HttpStatusCode.NotFound,
            HttpStatusCode.BadGateway,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.GatewayTimeout,
            HttpStatusCode.RequestTimeout,
        };

        public static async Task RefreshAccessToken(Session session, bool reportErrors = true)
        {
            var user = session.User;
            try
            {
                var json = new JsonObject
                {
                    ["data"] = user.RefreshToken,
                    ["path"] = session.ServerUri.AbsolutePath,
                    ["provider"] = "realm"
                };

                var result = await MakeAuthRequestAsync(user.ServerUri, json, TimeSpan.FromSeconds(30)).ConfigureAwait(continueOnCapturedContext: false);
                var access_token = result["access_token"];

                session.Handle.RefreshAccessToken(access_token["token"], access_token["token_data"]["path"]);
                ScheduleTokenRefresh(user.Identity, session.Path, _date_1970.AddSeconds(access_token["token_data"]["expires"]));
            }
            catch (HttpException ex) when (_connectivityStatusCodes.Contains(ex.StatusCode))
            {
                // 30 seconds is an arbitrarily chosen value, consider rationalizing it.
                ScheduleTokenRefresh(user.Identity, session.Path, DateTimeOffset.UtcNow.AddSeconds(30));
            }
            catch (Exception ex)
            {
                if (reportErrors)
                {
                    var sessionException = new SessionException("An error has occurred while refreshing the access token.",
                                                                ErrorCode.BadUserAuthentication,
                                                                ex);

                    Session.RaiseError(session, sessionException);
                }
            }
            finally
            {
                // session.User creates a new user each time, so it's safe to dispose the handle here.
                // It won't actually corrupt the state of the session.
                user.Handle.Dispose();
            }
        }

        // Returns a Tuple<userId, refreshToken>
        public static async Task<Tuple<string, string>> Login(Credentials credentials, Uri serverUrl)
        {
            var result = await MakeAuthRequestAsync(serverUrl, credentials.ToJson(), TimeSpan.FromSeconds(30)).ConfigureAwait(continueOnCapturedContext: false);
            var refresh_token = result["refresh_token"];
            return Tuple.Create((string)refresh_token["token_data"]["identity"], (string)refresh_token["token"]);
        }

        private static void ScheduleTokenRefresh(string userId, string path, DateTimeOffset expireDate)
        {
            var dueTime = expireDate.AddSeconds(-10) - DateTimeOffset.UtcNow;
            var timerState = new TokenRefreshData
            {
                RealmPath = path,
                UserId = userId
            };

            if (dueTime < TimeSpan.Zero)
            {
                OnTimerCallback(timerState);
            }

            _tokenRefreshTimers.AddOrUpdate(path, p =>
            {
                return new Timer(OnTimerCallback, timerState, dueTime, TimeSpan.FromMilliseconds(-1));
            }, (p, old) =>
            {
                old.Dispose();
                return new Timer(OnTimerCallback, timerState, dueTime, TimeSpan.FromMilliseconds(-1));
            });
        }

        private static void OnTimerCallback(object state)
        {
            var data = (TokenRefreshData)state;

            try
            {
                var user = User.GetLoggedInUser(data.UserId);
                if (user != null)
                {
                    var sessionPointer = user.Handle.GetSessionPointer(data.RealmPath);
                    var session = Session.Create(sessionPointer);
                    if (session != null)
                    {
                        RefreshAccessToken(session, reportErrors: false).ContinueWith(_ =>
                        {
                            user.Handle.Dispose();
                            session.Handle.Dispose();
                        });
                    }
                }
            }
            catch
            {
            }
            finally
            {
                Timer timer;
                if (_tokenRefreshTimers.TryRemove(data.RealmPath, out timer))
                {
                    timer.Dispose();
                }
            }
        }

        private static async Task<JsonValue> MakeAuthRequestAsync(Uri serverUri, JsonObject body, TimeSpan timeout)
        {
            body["app_id"] = string.Empty; // FIXME

            string requestBody;
            using (var writer = new StringWriter())
            {
                body.Save(writer);
                requestBody = writer.ToString();
            }

            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(serverUri, "auth"));
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            request.Headers.Accept.ParseAdd(_applicationJsonUtf8MediaType.MediaType);
            request.Headers.Accept.ParseAdd(_applicationProblemJsonUtf8MediaType.MediaType);

            var response = await _client.Value.SendAsync(request).ConfigureAwait(continueOnCapturedContext: false);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentType.Equals(_applicationJsonUtf8MediaType))
            {
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(continueOnCapturedContext: false))
                {
                    return JsonValue.Load(stream);
                }
            }

            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(continueOnCapturedContext: false))
            {
                if (response.Content.Headers.ContentType.Equals(_applicationProblemJsonUtf8MediaType))
                {
                    var problem = JsonValue.Load(stream);

                    var code = ErrorCodeHelper.GetErrorCode((int)problem["code"]) ?? ErrorCode.Unknown;

                    throw new AuthenticationException(code, response.StatusCode, response.ReasonPhrase, problem.ToString(), (string)problem["title"]);
                }

                var content = ReadContent(stream, ErrorContentTruncationLimit, $"Response too long. Truncated to first {ErrorContentTruncationLimit} characters:{Environment.NewLine}");
                throw new HttpException(response.StatusCode, response.ReasonPhrase, content);
            }
        }

        private static string ReadContent(Stream stream, int maxNumberOfCharacters, string prefixIfExceeded)
        {
            using (var sr = new StreamReader(stream))
            {
                var sb = new StringBuilder();

                int current;
                while ((current = sr.Read()) > 0)
                {
                    sb.Append((char)current);
                    if (sb.Length > maxNumberOfCharacters - 1)
                    {
                        sb.Insert(0, prefixIfExceeded);
                        break;
                    }
                }

                return sb.ToString();
            }
        }

        private class TokenRefreshData
        {
            public string UserId { get; set; }

            public string RealmPath { get; set; }
        }
    }
}