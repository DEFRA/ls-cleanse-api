using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Test.Storage;

/// <summary>The behaviour every <see cref="ISettingsStore"/> must have.</summary>
public abstract class SettingsStoreContractTests
{
    protected abstract ISettingsStore CreateStore();

    [Fact]
    public async Task Nothing_is_stored_until_the_first_save()
    {
        Assert.Null(await CreateStore().GetAsync());
    }

    [Fact]
    public async Task A_save_replaces_what_was_stored()
    {
        var store = CreateStore();

        await store.SaveAsync(new ServiceSettings(7));
        Assert.Equal(new ServiceSettings(7), await store.GetAsync());

        await store.SaveAsync(new ServiceSettings(0));
        Assert.Equal(new ServiceSettings(0), await store.GetAsync());
    }
}
