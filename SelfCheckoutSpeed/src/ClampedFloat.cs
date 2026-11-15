using System;
using BepInEx.Configuration;

namespace SelfCheckoutSpeed;

/// <summary>
/// Keeps a float setting between Min and Max, like <c>AcceptableValueRange&lt;float&gt;</c>, but without
/// the public <c>MinValue</c>/<c>MaxValue</c> properties. Config managers (Mod Settings Tool,
/// BepInExConfigManager) look for those properties to draw a slider; without them they draw a text
/// field you can type a number into, and BepInEx still clamps whatever is typed through
/// <see cref="Clamp"/>.
/// </summary>
internal sealed class ClampedFloat : AcceptableValueBase
{
    private readonly float _min;
    private readonly float _max;

    public ClampedFloat(float min, float max) : base(typeof(float))
    {
        _min = min;
        _max = max;
    }

    public override object Clamp(object value)
    {
        float v = value is float f ? f : Convert.ToSingle(value);
        if (float.IsNaN(v)) v = _min;
        return Math.Clamp(v, _min, _max);
    }

    public override bool IsValid(object value) =>
        value is float f && !float.IsNaN(f) && f >= _min && f <= _max;

    public override string ToDescriptionString() => $"# Acceptable value range: From {_min} to {_max}";
}
