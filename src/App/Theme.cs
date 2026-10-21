using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace AndroidTVDesktop
{
    /// <summary>
    /// Dark, TV-inspired Windows 11 style. Loose XAML parsed at runtime (no XAML compiler needed for this build).
    /// Colours: background #0E1014, surface #161A21, card #1D222B, accent #3B82F6, text #E8EAED / #9AA3AF.
    /// </summary>
    static class Theme
    {
        public static readonly Color Bg = Color.FromRgb(0x0E, 0x10, 0x14);
        public static readonly Color Surface = Color.FromRgb(0x16, 0x1A, 0x21);
        public static readonly Color Card = Color.FromRgb(0x1D, 0x22, 0x2B);
        public static readonly Color Border = Color.FromRgb(0x2A, 0x30, 0x3B);
        public static readonly Color Accent = Color.FromRgb(0x3B, 0x82, 0xF6);
        public static readonly Color Text = Color.FromRgb(0xE8, 0xEA, 0xED);
        public static readonly Color SubText = Color.FromRgb(0x9A, 0xA3, 0xAF);
        public static readonly Color Good = Color.FromRgb(0x22, 0xC5, 0x5E);
        public static readonly Color Warn = Color.FromRgb(0xF5, 0x9E, 0x0B);
        public static readonly Color Bad = Color.FromRgb(0xEF, 0x44, 0x44);

        public static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        public static ResourceDictionary Load() => (ResourceDictionary)XamlReader.Parse(Xaml);

        const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <SolidColorBrush x:Key='Bg' Color='#0E1014'/>
  <SolidColorBrush x:Key='Surface' Color='#161A21'/>
  <SolidColorBrush x:Key='Card' Color='#1D222B'/>
  <SolidColorBrush x:Key='CardHover' Color='#252B36'/>
  <SolidColorBrush x:Key='Line' Color='#2A303B'/>
  <SolidColorBrush x:Key='Accent' Color='#3B82F6'/>
  <SolidColorBrush x:Key='AccentHover' Color='#5B9BFF'/>
  <SolidColorBrush x:Key='Fg' Color='#E8EAED'/>
  <SolidColorBrush x:Key='Fg2' Color='#9AA3AF'/>

  <Style TargetType='TextBlock'>
    <Setter Property='Foreground' Value='{StaticResource Fg}'/>
    <Setter Property='FontFamily' Value='Segoe UI Variable Text, Segoe UI'/>
    <Setter Property='FontSize' Value='14'/>
    <Setter Property='TextWrapping' Value='Wrap'/>
  </Style>

  <Style x:Key='Btn' TargetType='Button'>
    <Setter Property='Foreground' Value='{StaticResource Fg}'/>
    <Setter Property='Background' Value='{StaticResource Card}'/>
    <Setter Property='BorderBrush' Value='{StaticResource Line}'/>
    <Setter Property='FontSize' Value='14'/>
    <Setter Property='Padding' Value='16,8'/>
    <Setter Property='Margin' Value='0,0,10,10'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='8' Padding='{TemplateBinding Padding}'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='{StaticResource CardHover}'/></Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{StaticResource Accent}'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter TargetName='b' Property='Opacity' Value='0.45'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='Button' BasedOn='{StaticResource Btn}'/>
  <Style x:Key='Primary' TargetType='Button' BasedOn='{StaticResource Btn}'>
    <Setter Property='Background' Value='{StaticResource Accent}'/>
    <Setter Property='BorderBrush' Value='{StaticResource Accent}'/>
    <Setter Property='Foreground' Value='White'/>
    <Setter Property='FontWeight' Value='SemiBold'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' Background='{TemplateBinding Background}' CornerRadius='8' Padding='{TemplateBinding Padding}'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='{StaticResource AccentHover}'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter TargetName='b' Property='Opacity' Value='0.45'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Nav' TargetType='RadioButton'>
    <Setter Property='Foreground' Value='{StaticResource Fg2}'/>
    <Setter Property='FontSize' Value='15'/>
    <Setter Property='Margin' Value='8,2'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RadioButton'>
          <Border x:Name='b' Background='Transparent' CornerRadius='8' Padding='14,10'>
            <Grid>
              <Border x:Name='bar' Width='3' Height='18' CornerRadius='2' Background='{StaticResource Accent}' HorizontalAlignment='Left' Margin='-10,0,0,0' Visibility='Hidden'/>
              <ContentPresenter VerticalAlignment='Center'/>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='#1A1F27'/></Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='b' Property='Background' Value='#202632'/>
              <Setter TargetName='bar' Property='Visibility' Value='Visible'/>
              <Setter Property='Foreground' Value='{StaticResource Fg}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='CheckBox'>
    <Setter Property='Foreground' Value='{StaticResource Fg}'/>
    <Setter Property='FontSize' Value='14'/>
    <Setter Property='Margin' Value='0,6'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='CheckBox'>
          <Grid Background='Transparent'>
            <Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition/></Grid.ColumnDefinitions>
            <Border x:Name='track' Width='40' Height='20' CornerRadius='10' Background='#2A303B' VerticalAlignment='Top' Margin='0,1,12,0'>
              <Ellipse x:Name='knob' Width='14' Height='14' Fill='#9AA3AF' HorizontalAlignment='Left' Margin='3,0'/>
            </Border>
            <ContentPresenter Grid.Column='1' VerticalAlignment='Center'/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='track' Property='Background' Value='{StaticResource Accent}'/>
              <Setter TargetName='knob' Property='HorizontalAlignment' Value='Right'/>
              <Setter TargetName='knob' Property='Fill' Value='White'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='RadioButton'>
    <Setter Property='Foreground' Value='{StaticResource Fg}'/>
    <Setter Property='FontSize' Value='14'/>
    <Setter Property='Margin' Value='0,4'/>
  </Style>

  <Style TargetType='TextBox'>
    <Setter Property='Background' Value='#11141A'/>
    <Setter Property='Foreground' Value='{StaticResource Fg}'/>
    <Setter Property='BorderBrush' Value='{StaticResource Line}'/>
    <Setter Property='CaretBrush' Value='{StaticResource Fg}'/>
    <Setter Property='SelectionBrush' Value='{StaticResource Accent}'/>
    <Setter Property='Padding' Value='8,6'/>
    <Setter Property='FontSize' Value='14'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='TextBox'>
          <Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='6'>
            <ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{StaticResource Accent}'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='ComboBoxItem'>
    <Setter Property='Foreground' Value='{StaticResource Fg}'/>
    <Setter Property='Padding' Value='10,7'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ComboBoxItem'>
          <Border x:Name='b' Background='Transparent' Padding='{TemplateBinding Padding}' CornerRadius='4'>
            <ContentPresenter/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='b' Property='Background' Value='#2A3442'/></Trigger>
            <Trigger Property='IsSelected' Value='True'><Setter TargetName='b' Property='Background' Value='#24406B'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='ComboBox'>
    <Setter Property='Foreground' Value='{StaticResource Fg}'/>
    <Setter Property='FontSize' Value='14'/>
    <Setter Property='MinWidth' Value='260'/>
    <Setter Property='HorizontalAlignment' Value='Left'/>
    <Setter Property='Margin' Value='0,4,0,8'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ComboBox'>
          <Grid>
            <ToggleButton IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}' Focusable='False' Cursor='Hand'>
              <ToggleButton.Template>
                <ControlTemplate TargetType='ToggleButton'>
                  <Border x:Name='b' Background='#11141A' BorderBrush='#2A303B' BorderThickness='1' CornerRadius='6'>
                    <Path HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,12,0' Data='M0,0 L5,5 L10,0' Stroke='#9AA3AF' StrokeThickness='1.6'/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='#3B82F6'/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter Margin='10,7,34,7' IsHitTestVisible='False' Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}' VerticalAlignment='Center'/>
            <Popup IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'>
              <Border Background='#1D222B' BorderBrush='#2A303B' BorderThickness='1' CornerRadius='6' Padding='4' MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}' MaxHeight='360'>
                <ScrollViewer><ItemsPresenter/></ScrollViewer>
              </Border>
            </Popup>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='ToolTip'>
    <Setter Property='Background' Value='#1D222B'/>
    <Setter Property='Foreground' Value='#E8EAED'/>
    <Setter Property='BorderBrush' Value='#2A303B'/>
  </Style>
</ResourceDictionary>";
    }
}
