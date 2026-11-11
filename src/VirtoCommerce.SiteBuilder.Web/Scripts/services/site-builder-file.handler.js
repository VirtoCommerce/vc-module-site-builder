var moduleName = "virtoCommerce.siteBuilderModule";

angular.module(moduleName)
    .factory('virtoCommerce.siteBuilderModule.fileHandler', [
        'platformWebApp.bladeNavigationService',
        'virtoCommerce.siteBuilderModule.contentApi',
        function (bladeNavigationService, siteBuilderApi) {
        var handler = {
            edit: {
                descriptor: {
                    icon: 'list-ico fa fa-crop',
                    name: 'siteBuilder.blades.siteBuilderEditor.edit.title',
                    description: 'siteBuilder.blades.siteBuilderEditor.edit.description'
                },
                isMatch: isMatchForEdit,
                execute: editFile
            },
            create: {
                descriptor: {
                    icon: 'list-ico fa fa-crop',
                    name: 'siteBuilder.blades.siteBuilderEditor.create.title',
                    description: 'siteBuilder.blades.siteBuilderEditor.create.description'
                },
                isMatch: function () { return true; },
                execute: createFile
            },
            duplicate: {
                descriptor: {
                    icon: 'list-ico fa fa-copy',
                    name: 'siteBuilder.blades.siteBuilderEditor.duplicate.title',
                    description: 'siteBuilder.blades.siteBuilderEditor.duplicate.description'
                },
                isMatch: isMatchForEdit,
                execute: duplicateFile
            }
        };

        function isMatchForEdit(file, operation) {
            return file && file.name && (file.name.endsWith('.page') || file.name.endsWith('.page-draft'));
        }

        function createFile(blade, parentBlade) {
            var newBlade = {
                id: 'designPage',
                contentType: blade.contentType,
                storeId: blade.storeId,
                storeUrl: blade.storeUrl,
                languages: blade.languages,
                folderUrl: blade.folderUrl,
                isNew: true,
                currentEntity: {},
                title: 'siteBuilder.blades.edit-page.title-new',
                subtitle: 'siteBuilder.blades.edit-page.subtitle-new',
                controller: 'virtoCommerce.siteBuilderModule.editPageController',
                template: 'Modules/$(VirtoCommerce.SiteBuilder)/Scripts/blades/pages/edit-page.tpl.html'
            };
            bladeNavigationService.showBlade(newBlade, parentBlade);
        }

        // Duplicating a page kept in a content repository is a commit under a new name on the
        // copying editor's work branch. The blob copy the content module would otherwise do writes
        // a "-draft" file instead — the blob flow's way of saying "not published yet" — and on this
        // flow that is a file no storefront serves, no publish picks up and no deploy removes.
        //
        // A store still on blob storage gets 404 from the endpoint, and the caller's own copy takes
        // over: correct for that store, and correct too where this module is newer than the server.
        function duplicateFile(context, done, notApplicable) {
            siteBuilderApi.gitCopy({
                storeId: context.blade.storeId,
                type: context.blade.contentType,
                srcPath: context.file.relativeUrl
            }, {}, done, function (error) {
                if (error.status === 404) {
                    notApplicable();
                    return;
                }
                var message = error.data && error.data.error ? error.data.error : 'Error ' + error.status;
                bladeNavigationService.setError(message, context.blade);
            });
        }

        function editFile(blade, parentBlade) {
            var newBlade = {
                id: 'designPage',
                contentType: blade.contentType,
                storeId: blade.storeId,
                storeUrl: blade.storeUrl,
                languages: blade.languages,
                folderUrl: blade.folderUrl,
                currentEntity: angular.copy(blade.currentEntity),
                isNew: false,
                title: 'siteBuilder.blades.edit-page.title-new',
                subtitle: 'siteBuilder.blades.edit-page.subtitle-new',
                controller: 'virtoCommerce.siteBuilderModule.editPageController',
                template: 'Modules/$(VirtoCommerce.SiteBuilder)/Scripts/blades/pages/edit-page.tpl.html'
            };

            bladeNavigationService.showBlade(newBlade, parentBlade);
        }

        return handler;

    }]);

