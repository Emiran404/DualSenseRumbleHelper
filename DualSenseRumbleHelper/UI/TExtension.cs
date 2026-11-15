using System.Windows.Data;
using System.Windows.Markup;
using DualSenseRumbleHelper.Services;

namespace DualSenseRumbleHelper.UI;

/// <summary>XAML: Text="{l:T steam.title}" binds to the translation, so a language switch updates it live.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) { Key = key; }

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
