using System;
using Avalonia.Controls;
using Avalonia.Platform;

namespace VivoPods.App.Services;

internal static class AppIcons
{
    public static WindowIcon Create()
    {
        using var stream = AssetLoader.Open(new Uri("avares://VivoPodsManager/Assets/app-icon.png"));
        return new WindowIcon(stream);
    }
}

