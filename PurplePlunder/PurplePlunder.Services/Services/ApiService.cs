using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reactive.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;
using DynamicData;
using Newtonsoft.Json.Linq;
using PurplePlunder.Core.Constants;
using PurplePlunder.Core.Enums;
using PurplePlunder.Core.Models;
using PurplePlunder.Services.Interfaces;
using PurplePlunder.Shared.Events;
using PurplePlunder.Shared.Extensions;

namespace PurplePlunder.Services.Services
{
    internal class ApiService : IApiService
    {
        #region Constants

        private const string GQL_URL = "https://gql.twitch.tv/gql";
        private const string AUTH_URL = "https://id.twitch.tv/oauth2/validate";
        private const string REVOKE_URL = "https://id.twitch.tv/oauth2/revoke";
        private const string USERS_URL = "https://api.twitch.tv/helix/users";
        private const string VIDEOS_URL = "https://api.twitch.tv/helix/videos";
        private const string CHANNELS_URL = "https://api.twitch.tv/helix/channels";
        private const string STREAMS_URL = "https://api.twitch.tv/helix/streams";
        private const string PLAYLISTS_URL = "https://usher.ttvnw.net/vod/{0}.m3u8";

        private const string PROCESSING_THUMBNAIL =
            "https://vod-secure.twitch.tv/_404/404_processing_320x180.png";

        private const int API_MAX_LOAD_LIMIT = 100;

        #endregion Constants

        #region Fields

        private readonly IRuntimeDataService _runtimeDataService;

        private readonly Regex _rxGroup = new Regex("GROUP-ID\\=\"(?<group>.*?)\\\"");
        private readonly Regex _rxName = new Regex("NAME\\=\"(?<name>.*?)\\\"");
        private readonly Regex _rxResolution = new Regex("RESOLUTION\\=(?<resolution>\\d+x\\d+)");
        private readonly Regex _rxCodecs = new Regex("CODECS\\=\"(?<codecs>.*?)\"");
        private IPreferencesService _preferencesService;
        private readonly ILogService _logService;
        private DateTime? _nextRequest;
        private readonly IEventAggregator _eventAggregator;

        #endregion Fields

        #region Constructors

        public ApiService(
            IRuntimeDataService runtimeDataService,
            IPreferencesService preferencesService,
            ILogService logService,
            IEventAggregator eventAggregator
        )
        {
            _runtimeDataService = runtimeDataService;
            _preferencesService = preferencesService;
            _logService = logService;
            _eventAggregator = eventAggregator;
        }

        #endregion Constructors

        #region Methods

        private WebClient CreateWebClientWithEncoding()
        {
            return new WebClient() { Encoding = Encoding.UTF8 };
        }

        private WebClient CreateApiWebClient()
        {
            WebClient wc = CreateWebClientWithEncoding();
            wc.Headers.Add("Client-ID", Constants.ClientId);
            wc.Headers.Add(
                "Authorization",
                $"Bearer {_runtimeDataService.RuntimeData.AuthInfo.AccessToken}"
            );

            return wc;
        }

        private WebClient CreateGqlWebClient()
        {
            WebClient wc = CreateWebClientWithEncoding();
            wc.Headers.Add("Client-ID", Constants.ClientIdWeb);

            string accessTokenSubOnly = _runtimeDataService.RuntimeData.AuthInfo.AccessTokenSubOnly;

            if (!string.IsNullOrWhiteSpace(accessTokenSubOnly))
            {
                wc.Headers.Add("Authorization", $"OAuth {accessTokenSubOnly}");
            }

            return wc;
        }

        private void SetNextRequestTime(WebClient wc)
        {
            string resetStr = wc.ResponseHeaders["Ratelimit-Reset"];

            if (!string.IsNullOrWhiteSpace(resetStr) && long.TryParse(resetStr, out long reset))
            {
                _nextRequest = DateTimeOffset.FromUnixTimeSeconds(reset).DateTime;
            }
        }

        private void WaitForNextRequest()
        {
            if (_nextRequest == null)
            {
                return;
            }

            DateTime utcNow = DateTime.UtcNow;

            if (utcNow < _nextRequest.Value)
            {
                Thread.Sleep((int)Math.Ceiling((_nextRequest.Value - utcNow).TotalMilliseconds));
            }
        }

        public bool ValidateAuthentication(string accessToken, bool subOnly)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return false;
            }

            using (WebClient webClient = CreateWebClientWithEncoding())
            {
                webClient.Headers.Add("Authorization", $"Bearer {accessToken}");

                string jsonStr = null;

                try
                {
                    jsonStr = webClient.DownloadString(AUTH_URL);
                }
                catch (WebException)
                {
                    // Any WebException indicates that the access token could not be verified
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(jsonStr))
                {
                    JObject json = JObject.Parse(jsonStr);

                    if (json != null)
                    {
                        string login = json.Value<string>("login");
                        string userId = json.Value<string>("user_id");
                        string clientId = json.Value<string>("client_id");

                        string checkClientId = subOnly ? Constants.ClientIdWeb : Constants.ClientId;

                        if (!subOnly && Constants.LegacyClientId.Equals(clientId, StringComparison.OrdinalIgnoreCase))
                        {
                            // Saved login from before Purple Plunder had its own Twitch app: clean it up
                            RevokeToken(accessToken, Constants.LegacyClientId);
                            return false;
                        }

                        if (
                            !string.IsNullOrWhiteSpace(login)
                            && !string.IsNullOrWhiteSpace(userId)
                            && !string.IsNullOrWhiteSpace(clientId)
                            && clientId.Equals(checkClientId, StringComparison.OrdinalIgnoreCase)
                        )
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        public void RevokeAuthentication(string accessToken, bool subOnly)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return;
            }

            RevokeToken(accessToken, subOnly ? Constants.ClientIdWeb : Constants.ClientId);
        }

        private void RevokeToken(string accessToken, string clientId)
        {
            using (WebClient webClient = CreateWebClientWithEncoding())
            {
                webClient.QueryString.Add("client_id", clientId);
                webClient.QueryString.Add("token", accessToken);

                try
                {
                    _ = webClient.UploadString(REVOKE_URL, string.Empty);
                }
                catch (WebException)
                {
                    // Ignore potentionally failed revoke requests
                }
            }
        }

        public bool ChannelExists(string channel)
        {
            if (string.IsNullOrWhiteSpace(channel))
            {
                throw new ArgumentNullException(nameof(channel));
            }

            return GetChannelIdByName(channel) != null;
        }

        public string GetChannelIdByName(string channel)
        {
            if (string.IsNullOrWhiteSpace(channel))
            {
                throw new ArgumentNullException(nameof(channel));
            }

            using (WebClient webClient = CreateApiWebClient())
            {
                webClient.QueryString.Add("login", channel);

                string result = null;

                try
                {
                    WaitForNextRequest();

                    result = webClient.DownloadString(USERS_URL);

                    SetNextRequestTime(webClient);
                }
                catch (WebException)
                {
                    return null;
                }

                if (!string.IsNullOrWhiteSpace(result))
                {
                    JObject searchResultJson = JObject.Parse(result);

                    JArray usersJson = searchResultJson.Value<JArray>("data");

                    if (usersJson != null && usersJson.HasValues)
                    {
                        JToken userJson = usersJson.FirstOrDefault();

                        if (userJson != null)
                        {
                            string id = userJson.Value<string>("id");

                            if (!string.IsNullOrWhiteSpace(id))
                            {
                                using (WebClient webClientChannel = CreateApiWebClient())
                                {
                                    webClientChannel.QueryString.Add("broadcaster_id", id);

                                    try
                                    {
                                        WaitForNextRequest();

                                        _ = webClientChannel.DownloadString(CHANNELS_URL);

                                        SetNextRequestTime(webClient);

                                        return id;
                                    }
                                    catch (WebException)
                                    {
                                        return null;
                                    }
                                    catch (Exception)
                                    {
                                        throw;
                                    }
                                }
                            }
                        }
                    }
                }

                return null;
            }
        }

        public ObservableCollection<Vod> Search(SearchParameters searchParams)
        {
            if (searchParams == null)
            {
                throw new ArgumentNullException(nameof(searchParams));
            }

            Debug.Assert(searchParams.SearchType != null, "searchParams.SearchType != null");
            SearchType searchType = searchParams.SearchType.Value;

            if (searchType == SearchType.Channel)
            {
                return SearchChannel(
                    searchParams.Channel,
                    searchParams.VideoType,
                    searchParams.LoadLimitType,
                    searchParams.LoadFrom.Value,
                    searchParams.LoadTo.Value,
                    searchParams.LoadLastVods
                );
            }
            else if (searchType == SearchType.Urls)
            {
                return SearchUrls(searchParams.Urls);
            }
            else if (searchType == SearchType.Favorites)
            {
                return SearchFavorites(
                    searchParams.VideoType,
                    searchParams.LoadFrom.Value,
                    searchParams.LoadTo.Value
                );
            }
            else
            {
                return SearchIds(searchParams.Ids);
            }
        }

        private ObservableCollection<Vod> SearchFavorites(
            VideoType videoType,
            DateTime loadFrom,
            DateTime loadTo
        )
        {
            var results = new ObservableCollection<Vod>();
            var favoriteChannels =
                _preferencesService.CurrentPreferences.SearchFavouriteChannels.ToArray();
            foreach (var channel in favoriteChannels)
            {
                if (!ChannelExists(channel))
                {
                    var message = $"Can't load videos from channel '{channel}'";
                    _eventAggregator.GetEvent<LogEvent>().PublishOnUiThread(message);
                    continue;
                }

                var videos = SearchChannel(
                    channel,
                    videoType,
                    LoadLimitType.Timespan,
                    loadFrom,
                    loadTo,
                    0
                );
                results.AddRange(videos);
            }

            results = new ObservableCollection<Vod>(
                results.OrderByDescending(video => video.RecordedDate)
            );

            return results;
        }

        private ObservableCollection<Vod> SearchChannel(
            string channel,
            VideoType videoType,
            LoadLimitType loadLimit,
            DateTime loadFrom,
            DateTime loadTo,
            int loadLastVods
        )
        {
            if (string.IsNullOrWhiteSpace(channel))
            {
                throw new ArgumentNullException(nameof(channel));
            }

            string channelId = GetChannelIdByName(channel);

            ObservableCollection<Vod> videos = new ObservableCollection<Vod>();

            string broadcastTypeParam;

            if (videoType == VideoType.Broadcast)
            {
                broadcastTypeParam = "archive";
            }
            else if (videoType == VideoType.Highlight)
            {
                broadcastTypeParam = "highlight";
            }
            else if (videoType == VideoType.Upload)
            {
                broadcastTypeParam = "upload";
            }
            else
            {
                throw new ApplicationException(
                    "Unsupported video type '" + videoType.ToString() + "'"
                );
            }

            DateTime fromDate = DateTime.Now;
            DateTime toDate = DateTime.Now;

            if (loadLimit == LoadLimitType.Timespan)
            {
                fromDate = loadFrom;
                toDate = loadTo;
            }

            int sum = 0;
            bool stop = false;
            string cursor = null;

            do
            {
                using (WebClient webClient = CreateApiWebClient())
                {
                    webClient.QueryString.Add("user_id", channelId);
                    webClient.QueryString.Add("type", broadcastTypeParam);
                    webClient.QueryString.Add("first", API_MAX_LOAD_LIMIT.ToString());

                    if (!string.IsNullOrWhiteSpace(cursor))
                    {
                        webClient.QueryString.Add("after", cursor);
                    }

                    WaitForNextRequest();

                    string result = webClient.DownloadString(VIDEOS_URL);

                    SetNextRequestTime(webClient);

                    JObject videosResponseJson = JObject.Parse(result);

                    if (videosResponseJson != null)
                    {
                        JArray videosJson = videosResponseJson.Value<JArray>("data");

                        if (videosJson.Count == 0)
                        {
                            stop = true;
                        }
                        else
                        {
                            JObject paginationJson = videosResponseJson.Value<JObject>(
                                "pagination"
                            );

                            if (paginationJson.ContainsKey("cursor"))
                            {
                                cursor = paginationJson.Value<string>("cursor");
                            }

                            foreach (JObject videoJson in videosJson)
                            {
                                sum++;

                                Vod video = ParseVideo(videoJson);

                                if (loadLimit == LoadLimitType.LastVods)
                                {
                                    videos.Add(video);

                                    if (sum >= loadLastVods)
                                    {
                                        stop = true;
                                        break;
                                    }
                                }
                                else
                                {
                                    DateTime recordedDate = video.RecordedDate;

                                    if (
                                        recordedDate.Date >= fromDate.Date
                                        && recordedDate.Date <= toDate.Date
                                    )
                                    {
                                        videos.Add(video);
                                    }

                                    if (recordedDate.Date < fromDate.Date)
                                    {
                                        stop = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            } while (!stop);

            return videos;
            return videos;
        }

        private ObservableCollection<Vod> SearchUrls(string urls)
        {
            if (string.IsNullOrWhiteSpace(urls))
            {
                throw new ArgumentNullException(nameof(urls));
            }

            ObservableCollection<Vod> videos = new ObservableCollection<Vod>();

            string[] urlArr = urls.Split(
                new string[] { Environment.NewLine },
                StringSplitOptions.RemoveEmptyEntries
            );

            if (urlArr.Length > 0)
            {
                HashSet<string> addedIds = new HashSet<string>();

                foreach (string url in urlArr)
                {
                    string id = GetVideoIdFromUrl(url);

                    if (!string.IsNullOrWhiteSpace(id) && !addedIds.Contains(id))
                    {
                        Vod video = GetVodFromId(id);

                        if (video != null)
                        {
                            TimeSpan? startTime = GetStartTimeFromUrl(url);

                            if (startTime.HasValue)
                            {
                                video.StartTime = startTime;
                            }

                            videos.Add(video);
                            addedIds.Add(id);
                        }
                    }
                }
            }

            return videos;
        }

        private ObservableCollection<Vod> SearchIds(string ids)
        {
            if (string.IsNullOrWhiteSpace(ids))
            {
                throw new ArgumentNullException(nameof(ids));
            }

            ObservableCollection<Vod> videos = new ObservableCollection<Vod>();

            string[] idsArr = ids.Split(
                new string[] { Environment.NewLine },
                StringSplitOptions.RemoveEmptyEntries
            );

            if (idsArr.Length > 0)
            {
                HashSet<string> addedIds = new HashSet<string>();

                foreach (string id in idsArr)
                {
                    if (!string.IsNullOrWhiteSpace(id) && !addedIds.Contains(id))
                    {
                        Vod video = GetVodFromId(id);

                        if (video != null)
                        {
                            videos.Add(video);
                            addedIds.Add(id);
                        }
                    }
                }
            }

            return videos;
        }

        public VodAuthInfo GetVodAuthInfo(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentNullException(nameof(id));
            }

            using (WebClient webClient = CreateGqlWebClient())
            {
                var gqlPlaybackAccessToken = CreateGqlPlaybackAccessToken(id);
                string accessTokenStr = webClient.UploadString(GQL_URL, gqlPlaybackAccessToken);

                JObject accessTokenJson = JObject.Parse(accessTokenStr);

                JToken vpaToken = accessTokenJson.SelectToken(
                    "$.data.videoPlaybackAccessToken",
                    false
                );

                if (vpaToken == null)
                {
                    throw new ApplicationException("The video playback access token is null!");
                }

                string token = Uri.EscapeDataString(vpaToken.Value<string>("value"));
                string signature = vpaToken.Value<string>("signature");

                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new ApplicationException("VOD access token is null!");
                }

                if (string.IsNullOrWhiteSpace(signature))
                {
                    throw new ApplicationException("VOD signature is null!");
                }

                bool privileged = false;
                bool subOnly = false;

                JObject tokenJson = JObject.Parse(HttpUtility.UrlDecode(token));

                if (tokenJson == null)
                {
                    throw new ApplicationException("Decoded VOD access token is null!");
                }

                privileged = tokenJson.Value<bool>("privileged");

                if (privileged)
                {
                    subOnly = true;
                }
                else
                {
                    JObject chansubJson = tokenJson.Value<JObject>("chansub");

                    if (chansubJson == null)
                    {
                        throw new ApplicationException("Token property 'chansub' is null!");
                    }

                    JArray restrictedQualitiesJson = chansubJson.Value<JArray>(
                        "restricted_bitrates"
                    );

                    if (restrictedQualitiesJson == null)
                    {
                        throw new ApplicationException(
                            "Token property 'chansub -> restricted_bitrates' is null!"
                        );
                    }

                    if (restrictedQualitiesJson.Count > 0)
                    {
                        subOnly = true;
                    }
                }

                return new VodAuthInfo(token, signature, privileged, subOnly);
            }
        }

        private string CreateGqlPlaybackAccessToken(string id)
        {
            //{
            //  "operationName": "PlaybackAccessToken_Template",
            //  "query": "query PlaybackAccessToken_Template($login: String!, $isLive: Boolean!, $vodID: ID!, $isVod: Boolean!, $playerType: String!) {  streamPlaybackAccessToken(channelName: $login, params: {platform: \"web\", playerBackend: \"mediaplayer\", playerType: $playerType}) @include(if: $isLive) {    value    signature    __typename  }  videoPlaybackAccessToken(id: $vodID, params: {platform: \"web\", playerBackend: \"mediaplayer\", playerType: $playerType}) @include(if: $isVod) {    value    signature    __typename  }}",
            //  "variables": {
            //    "isLive": false,
            //    "login": "",
            //    "isVod": true,
            //    "vodID": "1058435233",
            //    "playerType": "site"
            //  }
            //}

            return
                "{\"operationName\": \"PlaybackAccessToken_Template\", \"query\": \"query PlaybackAccessToken_Template($login: String!, $isLive: Boolean!, $vodID: ID!, $isVod: Boolean!, $playerType: String!) { streamPlaybackAccessToken(channelName: $login, params: {platform: \\\"web\\\", playerBackend: \\\"mediaplayer\\\", playerType: $playerType}) @include(if: $isLive) {    value    signature    __typename  }  videoPlaybackAccessToken(id: $vodID, params: {platform: \\\"web\\\", playerBackend: \\\"mediaplayer\\\", playerType: $playerType}) @include(if: $isVod) {    value    signature    __typename  }}\", \"variables\": { \"isLive\": false, \"login\": \"\", \"isVod\": true, \"vodID\": \""
                + id
                + "\", \"playerType\": \"site\" }}";
        }

        public Dictionary<VodQuality, string> GetPlaylistInfo(
            string vodId,
            VodAuthInfo vodAuthInfo
        )
        {
            using (WebClient webClient = CreateWebClientWithEncoding())
            {
                webClient.Headers.Add("Accept", "*/*");

                webClient.QueryString.Add("allow_source", "true");
                webClient.QueryString.Add("allow_audio_only", "true");
                // Twitch only lists 1440p (and higher) renditions, which are encoded with HEVC/AV1,
                // when the client announces that it supports those codecs.
                webClient.QueryString.Add("supported_codecs", "av1,h265,h264");
                webClient.QueryString.Add("platform", "web");
                webClient.QueryString.Add("player_backend", "mediaplayer");
                webClient.QueryString.Add("playlist_include_framerate", "true");
                webClient.QueryString.Add("token", vodAuthInfo.Token);
                webClient.QueryString.Add("sig", vodAuthInfo.Signature);

                string playlist = webClient.DownloadString(string.Format(PLAYLISTS_URL, vodId));

                List<string> lines = playlist
                    .Split(new string[] { "\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .ToList();

                Dictionary<VodQuality, string> playlistInfo =
                    new Dictionary<VodQuality, string>();

                for (int i = 0; i < lines.Count; i++)
                {
                    string line = lines[i];

                    if (!line.StartsWith("#") && i >= 2)
                    {
                        string mediaInfo = lines[i - 2];
                        string streamInfo = lines[i - 1];

                        Match groupMatch = _rxGroup.Match(mediaInfo);
                        string id = groupMatch.Groups["group"].Value;

                        Match nameMatch = _rxName.Match(mediaInfo);
                        string name = nameMatch.Groups["name"].Value;

                        string resolution = null;

                        Match resolutionMatch = _rxResolution.Match(streamInfo);

                        if (resolutionMatch.Success)
                        {
                            resolution = resolutionMatch.Groups["resolution"].Value;
                        }

                        string codecs = null;

                        Match codecsMatch = _rxCodecs.Match(streamInfo);

                        if (codecsMatch.Success)
                        {
                            codecs = codecsMatch.Groups["codecs"].Value;
                        }

                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        VodQuality quality = new VodQuality(id, name, resolution, codecs);

                        // Twitch lists renditions in order of preference, keep the first one per group
                        if (!playlistInfo.ContainsKey(quality))
                        {
                            playlistInfo.Add(quality, line);
                        }
                    }
                }

                AddHiddenSourceQuality(playlistInfo);

                return playlistInfo;
            }
        }

        /// <summary>
        /// For 1440p (HEVC) streams Twitch leaves the source quality out of the VOD playlist,
        /// but it is still stored next to the other qualities in a "chunked" folder.
        /// </summary>
        private void AddHiddenSourceQuality(Dictionary<VodQuality, string> playlistInfo)
        {
            if (playlistInfo.Keys.Any(q => q.IsSource))
            {
                return;
            }

            string anyPlaylist = playlistInfo
                .Where(e => !e.Key.IsAudioOnly)
                .Select(e => e.Value)
                .FirstOrDefault(v => v.StartsWith("http", StringComparison.OrdinalIgnoreCase));

            if (anyPlaylist == null)
            {
                return;
            }

            try
            {
                // https://cdn/<vod folder>/<quality>/index-dvr.m3u8
                Uri uri = new Uri(anyPlaylist);
                string path = uri.AbsolutePath;
                string vodFolder = path.Substring(0, path.LastIndexOf('/'));
                vodFolder = vodFolder.Substring(0, vodFolder.LastIndexOf('/'));
                string sourceBase = uri.GetLeftPart(UriPartial.Authority) + vodFolder + "/chunked/";
                string sourcePlaylist = sourceBase + "index-dvr.m3u8";

                using (WebClient webClient = CreateWebClientWithEncoding())
                {
                    string playlist = webClient.DownloadString(sourcePlaylist);

                    string mapLine = playlist
                        .Split('\n')
                        .FirstOrDefault(l => l.StartsWith("#EXT-X-MAP", StringComparison.OrdinalIgnoreCase));

                    Match mapMatch = mapLine == null ? Match.Empty : Regex.Match(mapLine, "URI=\"(?<uri>.*?)\"");

                    if (!mapMatch.Success)
                    {
                        // Without an init segment the resolution cannot be determined cheaply
                        return;
                    }

                    byte[] init = webClient.DownloadData(sourceBase + mapMatch.Groups["uri"].Value);

                    if (!Mp4InitInfo.TryRead(init, out int width, out int height, out string codec))
                    {
                        return;
                    }

                    // Only worth offering if it is better than what is listed
                    if (playlistInfo.Keys.Any(q => q.VerticalResolution >= height))
                    {
                        return;
                    }

                    VodQuality source = new VodQuality("chunked", "Source",
                        $"{width}x{height}", codec);

                    playlistInfo.Add(source, sourcePlaylist);
                }
            }
            catch (WebException)
            {
                // No hidden source quality, the listed qualities are still fine
            }
            catch (Exception ex)
            {
                _logService?.LogException(ex);
            }
        }

        private string GetVideoIdFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri validUrl))
            {
                return null;
            }

            string[] segments = validUrl.Segments;

            if (segments.Length < 2)
            {
                return null;
            }

            for (int i = 0; i < segments.Length; i++)
            {
                if (
                    segments[i].Equals("video/", StringComparison.OrdinalIgnoreCase)
                    || segments[i].Equals("videos/", StringComparison.OrdinalIgnoreCase)
                )
                {
                    if (segments.Length > (i + 1))
                    {
                        string idStr = segments[i + 1];

                        if (!string.IsNullOrWhiteSpace(idStr))
                        {
                            idStr = idStr.Trim(new char[] { '/' });

                            if (!string.IsNullOrWhiteSpace(idStr))
                            {
                                return idStr;
                            }
                        }
                    }

                    break;
                }
            }

            return null;
        }

        private TimeSpan? GetStartTimeFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri validUrl))
            {
                return null;
            }

            string query = validUrl.Query;

            if (!string.IsNullOrWhiteSpace(query))
            {
                var queryParams = HttpUtility.ParseQueryString(validUrl.Query);

                if (queryParams.ContainsKey("t"))
                {
                    string durationStr = queryParams["t"];

                    if (
                        !string.IsNullOrWhiteSpace(durationStr)
                        && TimeSpanExtensions.TryParseDurationFormat(
                            durationStr,
                            out TimeSpan startTime
                        )
                    )
                    {
                        return startTime;
                    }
                }
            }

            return null;
        }

        public Dictionary<string, LiveStreamInfo> GetLiveStreams(IEnumerable<string> logins)
        {
            Dictionary<string, LiveStreamInfo> result =
                new Dictionary<string, LiveStreamInfo>(StringComparer.OrdinalIgnoreCase);

            List<string> loginList = logins?
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Trim().ToLowerInvariant())
                .Distinct()
                .ToList() ?? new List<string>();

            // Helix accepts up to 100 logins per request
            for (int i = 0; i < loginList.Count; i += API_MAX_LOAD_LIMIT)
            {
                IEnumerable<string> batch = loginList.Skip(i).Take(API_MAX_LOAD_LIMIT);

                string url = STREAMS_URL + "?first=" + API_MAX_LOAD_LIMIT + "&" +
                             string.Join("&", batch.Select(l => "user_login=" + Uri.EscapeDataString(l)));

                using (WebClient webClient = CreateApiWebClient())
                {
                    WaitForNextRequest();

                    string json = webClient.DownloadString(url);

                    SetNextRequestTime(webClient);

                    JArray streamsJson = JObject.Parse(json).Value<JArray>("data");

                    if (streamsJson == null)
                    {
                        continue;
                    }

                    foreach (JToken streamJson in streamsJson)
                    {
                        if (!"live".Equals(streamJson.Value<string>("type"), StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string startedStr = streamJson.Value<string>("started_at");

                        DateTime startedAt = string.IsNullOrWhiteSpace(startedStr)
                            ? DateTime.UtcNow
                            : DateTime.Parse(startedStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

                        LiveStreamInfo info = new LiveStreamInfo(
                            streamJson.Value<string>("id"),
                            streamJson.Value<string>("user_id"),
                            streamJson.Value<string>("user_login"),
                            streamJson.Value<string>("user_name"),
                            streamJson.Value<string>("title"),
                            startedAt);

                        if (!string.IsNullOrWhiteSpace(info.UserLogin))
                        {
                            result[info.UserLogin] = info;
                        }
                    }
                }
            }

            return result;
        }

        public Vod GetArchiveVideoForStream(string userId, string streamId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                throw new ArgumentNullException(nameof(userId));
            }

            using (WebClient webClient = CreateApiWebClient())
            {
                webClient.QueryString.Add("user_id", userId);
                webClient.QueryString.Add("type", "archive");
                webClient.QueryString.Add("first", "5");

                WaitForNextRequest();

                string json = webClient.DownloadString(VIDEOS_URL);

                SetNextRequestTime(webClient);

                JArray videosJson = JObject.Parse(json).Value<JArray>("data");

                if (videosJson == null)
                {
                    return null;
                }

                foreach (JToken videoJson in videosJson)
                {
                    if (string.Equals(videoJson.Value<string>("stream_id"), streamId, StringComparison.Ordinal))
                    {
                        return ParseVideo(videoJson);
                    }
                }
            }

            return null;
        }

        private Vod GetVodFromId(string id)
        {
            using (WebClient webClient = CreateApiWebClient())
            {
                try
                {
                    webClient.QueryString.Add("id", id);

                    string result = webClient.DownloadString(VIDEOS_URL);

                    JObject videosResponseJson = JObject.Parse(result);

                    if (videosResponseJson != null)
                    {
                        JArray videosJson = videosResponseJson.Value<JArray>("data");

                        if (videosJson != null)
                        {
                            JToken videoJson = videosJson.FirstOrDefault();

                            if (videoJson != null)
                            {
                                return ParseVideo(videoJson);
                            }
                        }
                    }
                }
                catch (WebException ex)
                {
                    if (
                        ex.Response is HttpWebResponse resp
                        && resp.StatusCode == HttpStatusCode.NotFound
                    )
                    {
                        return null;
                    }
                    else
                    {
                        throw;
                    }
                }
            }

            return null;
        }

        public Vod ParseVideo(JToken videoJson)
        {
            string id = videoJson.Value<string>("id");
            string title = videoJson.Value<string>("title");
            string channel = videoJson.Value<string>("user_name");
            int views = videoJson.Value<int>("view_count");
            bool viewable = videoJson
                .Value<string>("viewable")
                .Equals("public", StringComparison.OrdinalIgnoreCase);
            string url = videoJson.Value<string>("url");
            string thumbnail = videoJson.Value<string>("thumbnail_url");
            TimeSpan length = TimeSpanExtensions.ParseDurationFormat(
                videoJson.Value<string>("duration")
            );
            JArray mutedSegments = videoJson.Value<JArray>("muted_segments");

            string recorded = videoJson.Value<string>("published_at");

            if (string.IsNullOrWhiteSpace(recorded))
            {
                recorded = videoJson.Value<string>("created_at");
            }

            DateTime recordedDate = DateTime.Parse(
                recorded,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal
            );

            bool muted = mutedSegments != null && mutedSegments.Count > 0;
            bool live = false;

            if (string.IsNullOrWhiteSpace(thumbnail))
            {
                thumbnail = PROCESSING_THUMBNAIL;
                live = true;
            }
            else
            {
                thumbnail = thumbnail.Replace("%{width}", "320").Replace("%{height}", "180");
            }

            return new Vod(
                channel,
                title,
                id,
                views,
                length,
                recordedDate,
                new Uri(thumbnail),
                new Uri(url),
                viewable,
                muted,
                live
            );
        }

        #endregion Methods
    }
}