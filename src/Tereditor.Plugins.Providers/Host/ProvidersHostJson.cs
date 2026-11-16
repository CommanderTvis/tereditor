using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Providers.Host;

[JsonSourceGenerationOptions(Converters = [typeof(PluginJson.EnumConverter<ProviderGroup>), typeof(PluginJson.EnumConverter<ModelAuthKind>)])]
[JsonSerializable(typeof(List<ProviderDefinition>))]
[JsonSerializable(typeof(List<ModelSourceChoice>))]
internal sealed partial class ProvidersHostJson : JsonSerializerContext;