﻿namespace OpenTelemetry.Proxy;

public class ActivityNameProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        if (!Sdk.SuppressInstrumentation) ActivityName.OnEnd(data);
    }
}
