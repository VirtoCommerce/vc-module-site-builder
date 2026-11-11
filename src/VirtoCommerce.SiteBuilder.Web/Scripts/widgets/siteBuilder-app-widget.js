angular.module('virtoCommerce.siteBuilderModule')
    .controller('virtoCommerce.siteBuilderModule.siteBuilderAppWidgetController', ['$scope', function ($scope) {
        $scope.openSiteBuilderApp = function () {
            var blade = $scope.blade;
            if (blade.currentEntityId) {
                var pathPrefix = window.location.pathname === '/' ? '' : window.location.pathname;
                var baseUrl = `${window.location.origin}${pathPrefix}/apps/site-builder-shell/`;

                window.open(`${baseUrl}?storeId=${blade.currentEntityId}`, '_blank');

            }
        }
    }]);
