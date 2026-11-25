using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Tereditor.Plugins.Chat.UI;

internal sealed partial class ChatSpinner : Border
{
    internal ChatSpinner() => AvaloniaXamlLoader.Load(this);
}