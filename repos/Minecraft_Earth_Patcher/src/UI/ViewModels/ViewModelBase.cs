using ReactiveUI;

namespace MCEPatcher.UI.ViewModels;

public class ViewModelBase : ReactiveObject
{
    public virtual void OnClose() { }
}
