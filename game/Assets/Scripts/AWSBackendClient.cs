using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

[Serializable]
public class AWSGameConfig
{
    public string gameName;
    public string region;
    public string protocol;
    public int maxPlayersPerMatch;
    public int defaultKillLimit;
    public AWSGameFeatures features;
}

[Serializable]
public class AWSGameFeatures
{
    public bool quickplay;
    public bool privateRooms;
    public bool leaderboard;
    public bool googleSignIn;
}

[Serializable]
public class AWSCreateRoomRequest
{
    public int maxPlayers;
    public int killLimit;
}

[Serializable]
public class AWSCreateRoomResponse
{
    public string roomCode;
    public string status;
    public int maxPlayers;
    public int killLimit;
    public string message;
}

[Serializable]
public class AWSEndpoint
{
    public string host;
    public int port;
    public string protocol;
}

[Serializable]
public class AWSAssignmentResponse
{
    public string status;
    public string roomCode;
    public AWSEndpoint endpoint;
    public string matchId;
    public string ticket;
    public long ticketExpiresAt;
}

[Serializable]
public class AWSQuickPlayRequest
{
    public string region;
}

[Serializable]
public class AWSErrorDetails
{
    public string code;
    public string message;
    public bool retryable;
}

[Serializable]
class AWSApiErrorResponse
{
    public AWSErrorDetails error;
}

public sealed class AWSApiResult<T>
{
    public bool Success { get; }
    public long StatusCode { get; }
    public T Data { get; }
    public string Error { get; }

    AWSApiResult(bool success, long statusCode, T data, string error)
    {
        Success = success;
        StatusCode = statusCode;
        Data = data;
        Error = error;
    }

    public static AWSApiResult<T> Ok(long statusCode, T data)
    {
        return new AWSApiResult<T>(true, statusCode, data, null);
    }

    public static AWSApiResult<T> Fail(long statusCode, string error)
    {
        return new AWSApiResult<T>(false, statusCode, default, error);
    }
}

public class AWSBackendClient : MonoBehaviour
{
    const string DefaultApiBaseUrl = "https://q9u78vshof.execute-api.ap-south-1.amazonaws.com";
    const int RequestTimeoutSeconds = 8;

    [SerializeField] string apiBaseUrl = DefaultApiBaseUrl;

    public AWSGameConfig Config { get; private set; }
    public bool ConfigIsFallback { get; private set; } = true;
    public string ConfigError { get; private set; }

    void Awake()
    {
        Config = CreateFallbackConfig();
    }

    async void Start()
    {
        var result = await RefreshConfigAsync();
        if (!result.Success)
            Debug.LogWarning("[AWS] Using local config fallback: " + result.Error);
    }

    public async Task<AWSApiResult<AWSGameConfig>> RefreshConfigAsync()
    {
        var result = await SendJsonAsync<AWSGameConfig>("GET", "/config", null);
        if (result.Success && IsValidConfig(result.Data))
        {
            Config = result.Data;
            ConfigIsFallback = false;
            ConfigError = null;
            return result;
        }

        string error = result.Success
            ? "The backend config did not match the supported schema."
            : result.Error;
        Config = CreateFallbackConfig();
        ConfigIsFallback = true;
        ConfigError = error;
        return AWSApiResult<AWSGameConfig>.Fail(result.StatusCode, error);
    }

    public async Task<AWSApiResult<AWSCreateRoomResponse>> CreateGuestRoomAsync(
        int maxPlayers,
        int killLimit,
        string idempotencyKey)
    {
        if (!IsValidIdempotencyKey(idempotencyKey))
            idempotencyKey = CreateIdempotencyKey();

        var request = new AWSCreateRoomRequest
        {
            maxPlayers = Mathf.Clamp(maxPlayers, 2, 10),
            killLimit = Mathf.Clamp(killLimit, 1, 200)
        };

        var result = await SendJsonAsync<AWSCreateRoomResponse>(
            "POST",
            "/guest/rooms",
            JsonUtility.ToJson(request),
            idempotencyKey);

        if (!result.Success)
            return result;
        if (string.IsNullOrWhiteSpace(result.Data.roomCode))
        {
            return AWSApiResult<AWSCreateRoomResponse>.Fail(
                result.StatusCode,
                "The backend response did not contain a room code.");
        }

        return result;
    }

    public async Task<AWSApiResult<AWSAssignmentResponse>> JoinGuestRoomAsync(
        string roomCode,
        string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(roomCode) || roomCode.Trim().Length != 6)
        {
            return AWSApiResult<AWSAssignmentResponse>.Fail(
                0,
                "Room code must be 6 characters.");
        }

        if (!IsValidIdempotencyKey(idempotencyKey))
            idempotencyKey = CreateIdempotencyKey();

        string normalized = roomCode.Trim().ToUpperInvariant();
        var result = await SendJsonAsync<AWSAssignmentResponse>(
            "POST",
            "/guest/rooms/" + Uri.EscapeDataString(normalized) + "/join",
            "{}",
            idempotencyKey);

        if (!result.Success)
            return result;

        if (result.Data == null || result.Data.endpoint == null || string.IsNullOrWhiteSpace(result.Data.endpoint.host) || result.Data.endpoint.port <= 0)
        {
            return AWSApiResult<AWSAssignmentResponse>.Fail(
                result.StatusCode,
                "The backend response did not contain a valid server endpoint.");
        }

        if (string.IsNullOrWhiteSpace(result.Data.ticket))
        {
            return AWSApiResult<AWSAssignmentResponse>.Fail(
                result.StatusCode,
                "The backend response did not contain a join ticket.");
        }

        return result;
    }

    public async Task<AWSApiResult<AWSAssignmentResponse>> QuickPlayAsync(
        string idempotencyKey,
        string region = null)
    {
        if (!IsValidIdempotencyKey(idempotencyKey))
            idempotencyKey = CreateIdempotencyKey();

        string body = string.IsNullOrWhiteSpace(region)
            ? "{}"
            : JsonUtility.ToJson(new AWSQuickPlayRequest { region = region.Trim() });

        var result = await SendJsonAsync<AWSAssignmentResponse>(
            "POST",
            "/guest/quickplay",
            body,
            idempotencyKey);

        if (!result.Success)
            return result;

        if (result.Data == null || result.Data.endpoint == null || string.IsNullOrWhiteSpace(result.Data.endpoint.host) || result.Data.endpoint.port <= 0)
        {
            return AWSApiResult<AWSAssignmentResponse>.Fail(
                result.StatusCode,
                "The backend response did not contain a valid server endpoint.");
        }

        if (string.IsNullOrWhiteSpace(result.Data.ticket))
        {
            return AWSApiResult<AWSAssignmentResponse>.Fail(
                result.StatusCode,
                "The backend response did not contain a join ticket.");
        }

        return result;
    }

    public static string CreateIdempotencyKey()
    {
        return Guid.NewGuid().ToString("N");
    }

    static bool IsValidIdempotencyKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length < 16 || key.Length > 128)
            return false;

        foreach (char character in key)
        {
            if (!((character >= 'A' && character <= 'Z') ||
                  (character >= 'a' && character <= 'z') ||
                  (character >= '0' && character <= '9') ||
                  character == '.' || character == '_' || character == '~' || character == '-'))
                return false;
        }

        return true;
    }

    async Task<AWSApiResult<T>> SendJsonAsync<T>(
        string method,
        string path,
        string jsonBody,
        string idempotencyKey = null)
    {
        string url = (apiBaseUrl ?? "").TrimEnd('/') + path;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return AWSApiResult<T>.Fail(0, "The backend API URL is invalid.");
        }

        using (var request = new UnityWebRequest(uri.AbsoluteUri, method))
        {
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = RequestTimeoutSeconds;

            if (jsonBody != null)
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
                request.SetRequestHeader("Content-Type", "application/json");
            }
            if (!string.IsNullOrEmpty(idempotencyKey))
                request.SetRequestHeader("Idempotency-Key", idempotencyKey);

            try
            {
                var operation = request.SendWebRequest();
                while (!operation.isDone)
                    await Task.Yield();
            }
            catch (InvalidOperationException)
            {
                return AWSApiResult<T>.Fail(request.responseCode, "The backend request could not be started.");
            }

            long statusCode = request.responseCode;
            string responseBody = request.downloadHandler.text;
            if (request.result != UnityWebRequest.Result.Success)
            {
                string message = TryReadApiError(responseBody);
                if (string.IsNullOrEmpty(message))
                    message = statusCode == 0
                        ? "The backend could not be reached."
                        : "The backend request failed (HTTP " + statusCode + ").";
                return AWSApiResult<T>.Fail(statusCode, message);
            }

            try
            {
                var data = JsonUtility.FromJson<T>(responseBody);
                if (data == null)
                    return AWSApiResult<T>.Fail(statusCode, "The backend returned an empty response.");
                return AWSApiResult<T>.Ok(statusCode, data);
            }
            catch (ArgumentException)
            {
                return AWSApiResult<T>.Fail(statusCode, "The backend returned invalid JSON.");
            }
        }
    }

    static bool IsValidConfig(AWSGameConfig config)
    {
        return config != null &&
               !string.IsNullOrWhiteSpace(config.gameName) &&
               !string.IsNullOrWhiteSpace(config.region) &&
               !string.IsNullOrWhiteSpace(config.protocol) &&
               config.maxPlayersPerMatch > 0 &&
               config.defaultKillLimit > 0 &&
               config.features != null;
    }

    static AWSGameConfig CreateFallbackConfig()
    {
        return new AWSGameConfig
        {
            gameName = "ACC Shooter",
            region = "ap-south-1",
            protocol = "udp",
            maxPlayersPerMatch = 10,
            defaultKillLimit = 30,
            features = new AWSGameFeatures()
        };
    }

    static string TryReadApiError(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;

        try
        {
            var errorResp = JsonUtility.FromJson<AWSApiErrorResponse>(responseBody);
            if (errorResp != null && errorResp.error != null)
            {
                if (!string.IsNullOrWhiteSpace(errorResp.error.message))
                    return errorResp.error.message;
                if (!string.IsNullOrWhiteSpace(errorResp.error.code))
                    return errorResp.error.code;
            }
        }
        catch (ArgumentException)
        {
        }

        return null;
    }
}
