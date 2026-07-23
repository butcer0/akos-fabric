using System.Diagnostics;

using OpenTelemetry;

namespace AkosFabric.Infrastructure.Telemetry;

internal sealed class MetadataOnlyActivityProcessor
    : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        foreach (KeyValuePair<string, object?> tag in data.TagObjects)
        {
            if (!MetadataOnlyTagPolicy.IsExportSafe(tag.Key))
            {
                data.SetTag(tag.Key, value: null);
            }
        }
    }
}
