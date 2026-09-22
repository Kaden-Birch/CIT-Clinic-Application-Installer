using System.ComponentModel;
using System.Runtime.CompilerServices;
using CITDeploy.Core;
namespace CITDeploy.Windows;
public sealed class StepViewModel(string name) : INotifyPropertyChanged
{
    public string Name { get; } = name; StepState state; string detail = "";
    public StepState State
    {
        get => state; set
        {
            state = value;
            Changed();
        }
    }
    public string Detail
    {
        get => detail; set
        {
            detail = value;
            Changed();
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public sealed class SessionViewModel : IDisposable
{
    public PortableStorage Storage
    {
        get;
    }
    readonly System.IO.FileStream kitLock;
    public CatalogRepository Repository
    {
        get;
    }
    public Catalog Catalog
    {
        get; private set;
    }
    public SessionViewModel(string? root = null)
    {
        Storage = new(root ?? AppContext.BaseDirectory);
        kitLock = new System.IO.FileStream(System.IO.Path.Combine(Storage.Root, ".CITDeploy.lock"), System.IO.FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None);
        Repository = new(Storage);
        Catalog = Repository.Load();
    }
    public void Save(string action)
    {
        try
        {
            Repository.Save(Catalog, action);
        }
        catch { Catalog = Repository.Load(); throw; }
    }
    public void Reload() => Catalog = Repository.Load();
    public void Dispose()
    {
        Repository.Dispose();
        kitLock.Dispose();
    }
}
