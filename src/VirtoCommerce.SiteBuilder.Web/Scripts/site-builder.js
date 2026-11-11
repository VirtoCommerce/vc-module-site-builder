//Call this to register our module to main application
var moduleTemplateName = "virtoCommerce.siteBuilderModule";

if (AppDependencies !== undefined) {
    AppDependencies.push(moduleTemplateName);
}

angular.module(moduleTemplateName, [])
    .config(
        ['virtoCommerce.contentModule.fileHandlerFactoryProvider',
            function (fileHandlerProvider) {
                fileHandlerProvider.addHandler('virtoCommerce.siteBuilderModule.fileHandler');
            }
        ]
    ).run(['platformWebApp.widgetService', function (widgetService) {
        //Register widget in store details
        widgetService.registerWidget({
            controller: 'virtoCommerce.siteBuilderModule.siteBuilderAppWidgetController',
            template: 'Modules/$(VirtoCommerce.SiteBuilder)/Scripts/widgets/siteBuilder-app-widget.html'
        }, 'storeDetail');
}]);
