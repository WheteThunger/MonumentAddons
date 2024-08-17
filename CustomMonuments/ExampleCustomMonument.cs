using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins;

[Info("Example Custom Monument", "WhiteThunder", "0.1.0")]
[Description("Example plugin which demonstrates how to define a custom monument for Monument Addons.")]
internal class ExampleCustomMonument : CovalencePlugin
{
    [PluginReference]
    private readonly Plugin MonumentAddons;

    private bool _isServerInitialized;

    private void OnServerInitialized()
    {
        _isServerInitialized = true;

        if (MonumentAddons != null)
        {
            RegisterCustomMonuments();
        }
    }

    private void Unload()
    {
        // Since this plugin creates new monument objects, it must clean them up when unloaded.
        foreach (var component in CustomMonumentComponent.InstanceList)
        {
            // Make sure to destroy the GameObject, not just the component.
            UnityEngine.Object.Destroy(component.gameObject);
        }
    }

    private void OnPluginLoaded(Plugin plugin)
    {
        if (!_isServerInitialized)
            return;

        if (plugin.Name == nameof(MonumentAddons))
        {
            RegisterCustomMonuments();
        }
    }

    private void RegisterCustomMonuments()
    {
        // Create and register a monument at (100, 0, 100) with 20x20x20 bounds.
        var smallMonument1 = CreateCustomMonument(AtTerrainHeight(new Vector3(100, 0, 100)), Quaternion.Euler(0, 0, 0));
        RegisterCustomMonument(smallMonument1, "MySmallMonument", new Bounds(new Vector3(0, 10, 0), new Vector3(20, 20, 20)));

        // Create and register a copy of the same monument at (200, 0, 200), with 90 degree rotation.
        // Any addon placed at one instance of Monument1 will automatically be present at all others.
        var smallMonument2 = CreateCustomMonument(AtTerrainHeight(new Vector3(200, 0, 200)), Quaternion.Euler(0, 90, 0));
        RegisterCustomMonument(smallMonument2, "MySmallMonument", new Bounds(new Vector3(0, 10, 0), new Vector3(20, 20, 20)));

        // Create and register a monument at (200, 0, 200) with 50x50x50 bounds.
        var largeMonument = CreateCustomMonument(AtTerrainHeight(new Vector3(300, 0, 300)), Quaternion.Euler(0, 0, 0));
        RegisterCustomMonument(largeMonument, "MyLargeMonument", new Bounds(new Vector3(0, 15, 0), new Vector3(50, 50, 50)));
    }

    private CustomMonumentComponent CreateCustomMonument(Vector3 position, Quaternion rotation)
    {
        var gameObject = new GameObject();
        gameObject.transform.SetPositionAndRotation(position, rotation);
        return gameObject.AddComponent<CustomMonumentComponent>();
    }

    private Vector3 AtTerrainHeight(Vector3 position)
    {
        return position.WithY(TerrainMeta.HeightMap.GetHeight(position));
    }

    private void RegisterCustomMonument(CustomMonumentComponent component, string name, Bounds bounds)
    {
        if (MonumentAddons.Call("API_RegisterCustomMonument", this, name, component, bounds) is not true)
        {
            LogError("Error registering custom monument. Monument Addons will have printed additional details in the logs.");
        }
    }

    private class CustomMonumentComponent : ListComponent<CustomMonumentComponent> {}
}
