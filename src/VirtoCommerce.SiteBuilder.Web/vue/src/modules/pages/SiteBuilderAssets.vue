<template>
  <AssetsLibraryBlade />
</template>

<script lang="ts" setup>
import { useBlade } from "@vc-shell/framework";
import AssetsLibraryBlade from "../asset-library/components/AssetsLibraryBlade.vue";
import { useUrlParams } from "../site-builder/composables";

defineBlade({
  name: "SiteBuilderAssets",
  url: "/site-builder-assets",
  permissions: "platform:asset:read",
});

const { param } = useBlade();
if (param.value) {
  useUrlParams().setStoreId(param.value);
}
</script>
