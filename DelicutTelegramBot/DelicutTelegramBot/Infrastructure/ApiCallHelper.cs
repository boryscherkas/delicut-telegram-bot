using System.Net;

namespace DelicutTelegramBot.Infrastructure;

/// <summary>
/// Wraps API calls to translate HTTP 401 into <see cref="DelicutAuthExpiredException"/>.
/// Shared by all services that call the Delicut API.
/// </summary>
public static class ApiCallHelper
{
    public static async Task<T> CallApiSafeAsync<T>(Func<Task<T>> apiCall)
    {
        try
        {
            return await apiCall();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new DelicutAuthExpiredException("Delicut token expired or invalid.", ex);
        }
    }
}
