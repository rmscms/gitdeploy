// Generates Themes/Controls.Midnight.xaml from the values of subtell/public/gitDeployPro/assets/css/core.css.
// Usage: node Themes/Builder/gen-skin.mjs
//
// Every number below is copied from core.css (.btn / .tab / .modal / ...). Colours are NOT written here: they come
// from the live palette brushes (Surface.*, Accent.*, State.* ...), so the skin follows any pack that selects it.
import { writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
mkdirSync(root, { recursive: true });

// ---- buttons ---------------------------------------------------------------------------------------------------
// bg/border/fg: resting; h*: hover; p*: pressed. "fill" = extra overlay used instead of changing the colour.
const R = (k) => `{StaticResource ${k}}`;
const variants = {
  Base:      { bg: R('Surface.Input'), bd: R('Border.Subtle'), fg: R('Text.Primary'),
               hbg: R('Surface.Raised'), hbd: R('Border.Strong'), hfg: R('Text.Primary'), pbg: R('State.Pressed') },
  Primary:   { bg: R('Accent.Primary'), bd: R('Accent.Primary'), fg: R('Skin.OnAccent'),
               hbg: R('Accent.PrimaryHover'), hbd: R('Accent.PrimaryHover'), hfg: R('Skin.OnAccent'), pbg: R('Accent.Primary'), glow: true },
  Secondary: { bg: R('Accent.Secondary'), bd: R('Accent.Secondary'), fg: R('Skin.OnAccent'),
               hbg: R('Accent.SecondaryHover'), hbd: R('Accent.SecondaryHover'), hfg: R('Skin.OnAccent'), pbg: R('Accent.Secondary') },
  Ghost:     { bg: 'Transparent', bd: 'Transparent', fg: R('Text.Secondary'),
               hbg: R('State.Hover'), hbd: 'Transparent', hfg: R('Text.Primary'), pbg: R('State.Pressed') },
  Success:   { bg: R('Status.Success'), bd: R('Status.Success'), fg: R('Skin.OnSuccess'),
               hbg: R('Status.Success'), hbd: R('Status.Success'), hfg: R('Skin.OnSuccess'), pbg: R('Status.Success'), brighten: true },
  Danger:    { bg: 'Transparent', bd: R('Status.Error'), fg: R('Status.Error'), bdOpacity: 0.45,
               hbg: R('Status.Error'), hbd: R('Status.Error'), hfg: R('Skin.OnAccent'), pbg: R('Status.Error') },
  DangerSolid: { bg: R('Status.Error'), bd: R('Status.Error'), fg: R('Skin.OnAccent'),
               hbg: R('Status.Error'), hbd: R('Status.Error'), hfg: R('Skin.OnAccent'), pbg: R('Status.Error'), brighten: true },
};

// size presets from core.css: .btn (36) / .compact (28) / .toolbar (30) / .icon (32)
const sizes = {
  Normal:  { h: 36, minW: 72, pad: '14,0', fs: '{StaticResource FontSize.Body}' },
  Compact: { h: 28, minW: 0,  pad: '10,0', fs: '12' },
  Toolbar: { h: 30, minW: 0,  pad: '10,0', fs: '{StaticResource FontSize.Body}' },
  Icon:    { h: 32, minW: 32, pad: '0',    fs: '{StaticResource FontSize.Body}', w: 32 },
};

function template(v, radiusKey) {
  const dangerOutline = v.bdOpacity != null;
  const frame = dangerOutline
    ? `<Border x:Name="Frame" BorderBrush="${v.bd}" BorderThickness="1" Opacity="${v.bdOpacity}" CornerRadius="${radiusKey}" />`
    : '';
  const bdBrush = dangerOutline ? 'Transparent' : '{TemplateBinding BorderBrush}';
  return `
                <ControlTemplate TargetType="Button">
                    <Grid x:Name="Root" SnapsToDevicePixels="True">
                        ${frame}
                        <Border x:Name="Bd"
                                Background="{TemplateBinding Background}"
                                BorderBrush="${bdBrush}"
                                BorderThickness="{TemplateBinding BorderThickness}"
                                CornerRadius="${radiusKey}">
                            <ContentPresenter x:Name="Content" Margin="{TemplateBinding Padding}"
                                              HorizontalAlignment="Center" VerticalAlignment="Center"
                                              RecognizesAccessKey="True" />
                        </Border>
                        <Border x:Name="Shine" Background="White" Opacity="0" IsHitTestVisible="False"
                                CornerRadius="${radiusKey}" />
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="${v.hbg}" />
                            ${dangerOutline ? `<Setter TargetName="Frame" Property="Opacity" Value="1" />` : `<Setter TargetName="Bd" Property="BorderBrush" Value="${v.hbd}" />`}
                            <Setter Property="Foreground" Value="${v.hfg}" />
                            ${v.glow ? `<Setter TargetName="Root" Property="Effect" Value="{StaticResource Skin.AccentGlowEffect}" />` : ''}
                            ${v.brighten ? `<Setter TargetName="Shine" Property="Opacity" Value="0.1" />` : ''}
                        </Trigger>
                        <Trigger Property="IsPressed" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="${v.pbg}" />
                            ${v.brighten ? `<Setter TargetName="Shine" Property="Opacity" Value="0.04" />` : ''}
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter TargetName="Root" Property="Opacity" Value="0.4" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>`;
}

function buttonStyle(key, basedOn, variantKey, sizeKey, extra = '', radius = '{StaticResource Radius.Button}') {
  const v = variants[variantKey];
  const s = sizes[sizeKey];
  return `
    <Style x:Key="${key}" TargetType="Button"${basedOn ? ` BasedOn="{StaticResource ${basedOn}}"` : ''}>
        <Setter Property="FontFamily" Value="{StaticResource FontFamily.App}" />
        <Setter Property="FontSize" Value="${s.fs}" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="Height" Value="${s.h}" />
        <Setter Property="MinWidth" Value="${s.minW}" />
        ${s.w ? `<Setter Property="Width" Value="${s.w}" />` : ''}
        <Setter Property="Padding" Value="${s.pad}" />
        <Setter Property="Foreground" Value="${v.fg}" />
        <Setter Property="Background" Value="${v.bg}" />
        <Setter Property="BorderBrush" Value="${v.bd}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="FocusVisualStyle" Value="{StaticResource Skin.FocusVisual}" />
        <Setter Property="SnapsToDevicePixels" Value="True" />${extra}
        <Setter Property="Template">
            <Setter.Value>${template(v, radius)}
            </Setter.Value>
        </Setter>
    </Style>`;
}

let out = `<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:mah="http://metro.mahapps.com/winfx/xaml/controls">
    <!--
        GENERATED by Themes/Builder/gen-skin.mjs from subtell/public/gitDeployPro/assets/css/core.css; do not edit by hand.
        Midnight Blue control styles: merged AFTER Themes/Controls.xaml in App.xaml, so every key below replaces
        the default one for pages/windows that resolve it afterwards.
        Rule: redefine a whole style FAMILY, because BasedOn inside Controls.xaml is already bound to the defaults.
    -->

    <!-- fixed foregrounds that sit on saturated fills (core.css: .btn.primary #fff, .btn.success #03150b) -->
    <SolidColorBrush x:Key="Skin.OnAccent" Color="#FFFFFF" />
    <SolidColorBrush x:Key="Skin.OnSuccess" Color="#03150B" />

    <!-- core.css .btn.primary:hover: box-shadow 0 0 18px accent-glow -->
    <DropShadowEffect x:Key="Skin.AccentGlowEffect"
                      BlurRadius="18" ShadowDepth="0" Opacity="1"
                      Color="{Binding Color, Source={StaticResource Accent.Glow}}" />

    <!-- core.css focus-visible: 2px solid accent outline, offset 1px -->
    <Style x:Key="Skin.FocusVisual">
        <Setter Property="Control.Template">
            <Setter.Value>
                <ControlTemplate>
                    <Rectangle Margin="-2" RadiusX="9" RadiusY="9" StrokeThickness="2"
                               Stroke="{StaticResource Accent.Primary}" SnapsToDevicePixels="True" />
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
`;

out += buttonStyle('App.Button.Base', null, 'Base', 'Normal');
out += buttonStyle('App.Button.Primary', 'App.Button.Base', 'Primary', 'Normal');
out += buttonStyle('App.Button.Secondary', 'App.Button.Base', 'Secondary', 'Normal');
out += buttonStyle('App.Button.Ghost', 'App.Button.Base', 'Ghost', 'Normal');
out += buttonStyle('App.Button.Success', 'App.Button.Base', 'Success', 'Normal');
out += buttonStyle('App.Button.Danger', 'App.Button.Base', 'Danger', 'Normal');
out += buttonStyle('App.Button.Danger.Solid', 'App.Button.Base', 'DangerSolid', 'Normal');
out += buttonStyle('App.Button.Compact', 'App.Button.Base', 'Base', 'Compact');
out += buttonStyle('App.Button.Toolbar', 'App.Button.Base', 'Ghost', 'Toolbar');
out += buttonStyle('App.Button.Pill', 'App.Button.Base', 'Base', 'Compact', '', '{StaticResource Radius.Chip}');
out += buttonStyle('App.IconButton', 'App.Button.Ghost', 'Ghost', 'Icon');
out += `
    <Style x:Key="App.IconButton.Close" TargetType="Button" BasedOn="{StaticResource App.IconButton}">
        <Setter Property="Width" Value="30" />
        <Setter Property="Height" Value="30" />
        <Setter Property="MinWidth" Value="30" />
        <Setter Property="Foreground" Value="{StaticResource Text.Muted}" />
    </Style>

    <!-- implicit style: every un-styled Button follows the skin's ghost button (core.css .btn.ghost) -->
    <Style TargetType="Button" BasedOn="{StaticResource App.Button.Ghost}" />

    <!-- core.css .tab: 38px, 14px padding, 600, muted -> primary on hover (+hover tint), accent + 2px underline when active -->
    <Style x:Key="App.TabItemStyle" TargetType="TabItem">
        <Setter Property="FontFamily" Value="{StaticResource FontFamily.App}" />
        <Setter Property="FontSize" Value="{StaticResource FontSize.Body}" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="Foreground" Value="{StaticResource Text.Muted}" />
        <Setter Property="Height" Value="38" />
        <Setter Property="Padding" Value="14,0" />
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="TabItem">
                    <Border x:Name="TabBorder"
                            Background="{TemplateBinding Background}"
                            BorderBrush="Transparent"
                            BorderThickness="0,0,0,2">
                        <ContentPresenter ContentSource="Header" Margin="{TemplateBinding Padding}"
                                          HorizontalAlignment="Center" VerticalAlignment="Center" />
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="TabBorder" Property="Background" Value="{StaticResource State.Hover}" />
                            <Setter Property="Foreground" Value="{StaticResource Text.Primary}" />
                        </Trigger>
                        <Trigger Property="IsSelected" Value="True">
                            <Setter TargetName="TabBorder" Property="BorderBrush" Value="{StaticResource Accent.Primary}" />
                            <Setter Property="Foreground" Value="{StaticResource Accent.Primary}" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- core.css .modal: radius 14, border-strong, raised surface -->
    <Style x:Key="App.ModalShell" TargetType="Border">
        <Setter Property="Background" Value="{StaticResource Surface.Raised}" />
        <Setter Property="BorderBrush" Value="{StaticResource Border.Strong}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="CornerRadius" Value="14" />
        <Setter Property="Padding" Value="22" />
    </Style>

    <!-- core.css inputs: 36px, radius 8, 12px padding; hover border-strong; focus border-accent -->
    <Style x:Key="App.InputTextBox" TargetType="TextBox">
        <Setter Property="Height" Value="36" />
        <Setter Property="Padding" Value="12,0" />
        <Setter Property="FontFamily" Value="{StaticResource FontFamily.App}" />
        <Setter Property="FontSize" Value="{StaticResource FontSize.Body}" />
        <Setter Property="Foreground" Value="{StaticResource Text.Primary}" />
        <Setter Property="Background" Value="{StaticResource Surface.Input}" />
        <Setter Property="BorderBrush" Value="{StaticResource Border.Subtle}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="VerticalContentAlignment" Value="Center" />
        <Setter Property="CaretBrush" Value="{StaticResource Text.Primary}" />
        <Setter Property="SelectionBrush" Value="{StaticResource Accent.Primary}" />
        <Setter Property="mah:ControlsHelper.CornerRadius" Value="{StaticResource Radius.Input}" />
        <Setter Property="mah:ControlsHelper.MouseOverBorderBrush" Value="{StaticResource Border.Strong}" />
        <Setter Property="mah:ControlsHelper.FocusBorderBrush" Value="{StaticResource Accent.Primary}" />
    </Style>
    <Style TargetType="TextBox" BasedOn="{StaticResource App.InputTextBox}" />
</ResourceDictionary>
`;

writeFileSync(join(root, 'Controls.Midnight.xaml'), out.replace(/\n[ ]+\n/g, '\n'), 'utf8');
console.log('Controls.Midnight.xaml written:', join(root, 'Controls.Midnight.xaml'));
