using Microsoft.ML.Transforms;

namespace RugPullShared;

public class LabelOutput
{
    public bool Label { get; set; }
}

/// <summary>
/// Converts the SolRPDS "INACTIVITY_STATUS" column to a boolean label.
/// "Inactive" pools are treated as rug pulls (Label = true).
/// </summary>
[CustomMappingFactoryAttribute("InactivityToLabel")]
public class InactivityMapping : CustomMappingFactory<RugPullData, LabelOutput>
{
    public override Action<RugPullData, LabelOutput> GetMapping()
        => (input, output) => output.Label = input.InactivityStatus == "Inactive";
}
