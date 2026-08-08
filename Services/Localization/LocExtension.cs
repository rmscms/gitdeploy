using System;
using System.Windows.Markup;

namespace GitDeployPro.Services.Localization
{
    /// <summary>
    /// XAML: ToolTip="{loc:T deploy.tip.terminal}"
    /// Class must be named TExtension for the {loc:T ...} syntax.
    /// </summary>
    [MarkupExtensionReturnType(typeof(object))]
    public sealed class TExtension : MarkupExtension
    {
        public TExtension()
        {
        }

        public TExtension(string key)
        {
            Key = key;
        }

        [ConstructorArgument("key")]
        public string Key { get; set; } = string.Empty;

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            if (string.IsNullOrWhiteSpace(Key))
            {
                return string.Empty;
            }

            var binding = new System.Windows.Data.Binding($"[{Key}]")
            {
                Source = LocalizationService.Instance,
                Mode = System.Windows.Data.BindingMode.OneWay
            };

            if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget
                {
                    TargetObject: System.Windows.DependencyObject
                })
            {
                return binding.ProvideValue(serviceProvider);
            }

            // Design-time / non-DependencyObject: resolve immediately.
            return LocalizationService.Instance.Get(Key);
        }
    }
}
