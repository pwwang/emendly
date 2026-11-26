using System.Windows.Forms;

namespace Emendly.App.Extensions;

public interface IEmendlyExtension
{
    string Name { get; }

    void ConfigureTrayMenu(ContextMenuStrip menu)
    {
    }

    TransformRequest? TransformRequestFilter(TransformRequest request) => request;

    void OnTransformCompleted(TransformRequest request, TransformResult result, string? appProcess)
    {
    }
}
