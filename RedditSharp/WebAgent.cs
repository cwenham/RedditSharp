using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Collections.Generic;

namespace RedditSharp
{
    /// <inheritdoc />
    public class WebAgent : IWebAgent
    {

        private const string OAuthDomainUrl = "oauth.reddit.com";
        private static HttpClient _httpClient;

        /// <summary>
        /// Additional values to append to the default RedditSharp user agent.
        /// </summary>
        public static string UserAgent { get; set; }

        /// <summary>
        /// web protocol "http", "https"
        /// </summary>
        public static string Protocol { get; set; }

        /// <summary>
        /// The root domain RedditSharp uses to address Reddit.
        /// www.reddit.com by default
        /// </summary>
        public static string RootDomain { get; set; }

        /// <inheritdoc />
        public string AccessToken { get; set; }

        private static SemaphoreSlim rateLimitLock;
        // See https://github.com/reddit/reddit/wiki/API for more details.
        public static int RateLimitUsed { get; set; }
        public static int RateLimitRemaining { get; set; }
        // Approximate seconds until the rate limit is reset.
        public static int RateLimitReset { get; set;}

        static WebAgent() {
            //Static constructors are dumb, no likey -Meepster23
            rateLimitLock = new SemaphoreSlim(1, 1);
            UserAgent = string.IsNullOrWhiteSpace( UserAgent ) ? "" : UserAgent ;
            Protocol = string.IsNullOrWhiteSpace(Protocol) ? "https" : Protocol;
            RootDomain = string.IsNullOrWhiteSpace(RootDomain) ? "www.reddit.com" : RootDomain;
            _httpClient = new HttpClient();
        }

        /// <summary>
        ///
        /// </summary>
        public WebAgent() {
        }

        /// <summary>
        /// Intializes a WebAgent with a specified access token and sets the default url to the oauth api address
        /// </summary>
        /// <param name="accessToken">Valid access token</param>
        public WebAgent( string accessToken ) {
            RootDomain = OAuthDomainUrl;
            AccessToken = accessToken;
            RateLimitUsed = 0;
            RateLimitReset = 60;
            RateLimitRemaining = IsOAuth() ? 60 : 30;
        }

        /// <inheritdoc />
        public virtual async Task<JToken> ExecuteRequestAsync(HttpRequestMessage request)
        {
            const int maxTries = 20;
            HttpResponseMessage response;
            var tries = 0;
            do {
              if (RateLimitRemaining <= 0) {
                await Task.Delay(RateLimitReset * 1000);
              }
              response = await _httpClient.SendAsync(request).ConfigureAwait(false);
              await rateLimitLock.WaitAsync().ConfigureAwait(false);
              try {
                IEnumerable<string> values;
                var headers = response.Headers;
                if (headers.TryGetValues("X-Ratelimit-Used", out values))
                  RateLimitUsed = int.Parse(values.First());
                if (headers.TryGetValues("X-Ratelimit-Remaining", out values))
                  RateLimitRemaining = (int)double.Parse(values.First());
                if (headers.TryGetValues("X-Ratelimit-Reset", out values))
                  RateLimitReset = int.Parse(values.First());
                Console.WriteLine(RateLimitUsed.ToString());
                Console.WriteLine(RateLimitRemaining.ToString());
                Console.WriteLine(RateLimitReset.ToString());
              } finally {
                rateLimitLock.Release();
              }
              tries++;
              if (!response.IsSuccessStatusCode)
                Console.WriteLine(response.ToString());
            } while(!response.IsSuccessStatusCode && tries < maxTries);
            var result = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            JToken json;
            if (!string.IsNullOrEmpty(result))
            {
                json = JToken.Parse(result);
                try
                {
                    if (json["json"] != null)
                    {
                        json = json["json"]; //get json object if there is a root node
                    }
                    if (json["error"] != null)
                    {
                        switch (json["error"].ToString())
                        {
                            case "404":
                                throw new Exception("File Not Found");
                            case "403":
                                throw new Exception("Restricted");
                            case "invalid_grant":
                                //Refresh authtoken
                                //AccessToken = authProvider.GetRefreshToken();
                                //ExecuteRequest(request);
                                break;
                        }
                    }
                }
                catch
                {
                }
            }
            else
            {
                json = JToken.Parse("{'method':'" + response.RequestMessage.Method + "','uri':'" + response.RequestMessage.RequestUri.AbsoluteUri + "','status':'" + response.StatusCode.ToString() + "'}");
            }
            return json;
        }

        /// <inheritdoc />
        public virtual HttpRequestMessage CreateRequest(string url, string method)
        {
            bool prependDomain;
            // IsWellFormedUristring returns true on Mono for some reason when using a string like "/api/me"
            if (Type.GetType("Mono.Runtime") != null)
                prependDomain = !url.StartsWith("http://") && !url.StartsWith("https://");
            else
                prependDomain = !Uri.IsWellFormedUriString(url, UriKind.Absolute);

            HttpRequestMessage request = new HttpRequestMessage();
            if (prependDomain)
            {
                request.RequestUri = new Uri(string.Format("{0}://{1}{2}", Protocol, RootDomain, url));
            }
            else
            {
                request.RequestUri = new Uri(url);
            }

            if (IsOAuth())// use OAuth
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("bearer", AccessToken);//Must be included in OAuth calls
            }

            request.Method = new HttpMethod(method);
            request.Headers.UserAgent.ParseAdd(UserAgent + " - with RedditSharp by meepster23");
            return request;
        }

        /// <inheritdoc />
        protected virtual HttpRequestMessage CreateRequest(Uri uri, string method)
        {
            var request = new HttpRequestMessage();
            request.RequestUri = uri;
            if (IsOAuth())// use OAuth
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("bearer", AccessToken);//Must be included in OAuth calls
            }

            request.Method = new HttpMethod(method);
            request.Headers.UserAgent.ParseAdd(UserAgent + " - with RedditSharp by /u/meepster23");
            return request;
        }

        /// <inheritdoc />
        public async Task<JToken> Get(string url)
        {
            var request = CreateRequest(url, "GET");
            return await ExecuteRequestAsync(request).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<JToken> Post(string url, object data, params string[] additionalFields)
        {
            var request = CreateRequest(url, "POST");
            WritePostBody(request, data, additionalFields);
            return await ExecuteRequestAsync(request).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<JToken> Put(string url, object data)
        {
            var request = CreateRequest(url, "POST");
            WritePostBody(request, data);
            return await ExecuteRequestAsync(request).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public virtual void WritePostBody(HttpRequestMessage request, object data, params string[] additionalFields)
        {
            var type = data.GetType();
            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var content = new List<KeyValuePair<string, string>>();
            foreach (var property in properties)
            {
                var attr = property.GetCustomAttributes(typeof(RedditAPINameAttribute), false).FirstOrDefault() as RedditAPINameAttribute;
                string name = attr == null ? property.Name : attr.Name;
                var entry = Convert.ToString(property.GetValue(data, null));
                content.Add(new KeyValuePair<string,string>(name, entry));
            }
            for (int i = 0; i < additionalFields.Length; i += 2)
            {
                var entry = Convert.ToString(additionalFields[i + 1]) ?? string.Empty;
                content.Add(new KeyValuePair<string, string>(additionalFields[i], entry));
            }

            request.Content = new FormUrlEncodedContent(content);
        }

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetResponseAsync(HttpRequestMessage message) {
          return _httpClient.SendAsync(message);
        }

        private static bool IsOAuth() => RootDomain == "oauth.reddit.com";

    }
}
