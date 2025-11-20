using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace TraceFox___Debugger.Extensions
{
    public static class SessionExtensions
    {
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public static void Set<T>(this ISession session, string key, T value)
        {
            session.SetString(key, JsonSerializer.Serialize(value, _jsonOptions));
        }

        public static T? Get<T>(this ISession session, string key)
        {
            var value = session.GetString(key);
            return value == null ? default : JsonSerializer.Deserialize<T>(value, _jsonOptions);
        }
    }
}