using VpnHood.AppLib.Abstractions;
using VpnHood.Core.Client.Device.UiContexts;
using VpnHood.Core.Toolkit.Utils;

namespace VpnHood.AppLib.Services.Accounts;

public class AppBillingService(AppAccountService accountService, IAppBillingProvider billingProvider) : IDisposable
{
    public BillingPurchaseState PurchaseState => billingProvider.PurchaseState;
    public string ProviderName => billingProvider.ProviderName;

    public Task<SubscriptionPlan[]> GetSubscriptionPlans()
    {
        return billingProvider.GetSubscriptionPlans();
    }

    public async Task<string> Purchase(IUiContext uiContext, string planId)
    {
        var ret = await billingProvider.Purchase(uiContext, planId).Vhc();
        await accountService.Refresh(updateCurrentClientProfile: true).Vhc();
        return ret;
    }

    public void Dispose()
    {
        billingProvider.Dispose();
    }
}