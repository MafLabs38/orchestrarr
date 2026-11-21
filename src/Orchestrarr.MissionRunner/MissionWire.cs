using System.Text.Json;

namespace Orchestrarr.MissionRunner;

internal static class MissionWire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
