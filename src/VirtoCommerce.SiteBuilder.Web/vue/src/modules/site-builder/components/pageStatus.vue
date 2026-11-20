<template>
  <div class="tw-flex tw-flex-row tw-gap-1">
    <VcStatus
      v-if="item.status"
      class="w-auto"
      v-bind="statusStyles[item.status]"
      >{{ $t(`SITE_BUILDER.STATUS.${item.status.toUpperCase()}`) }}</VcStatus
    >

    <template v-if="item.hasChanges && item.status == PageStatuses.Published">
      <VcStatus
        class="w-auto"
        v-bind="statusStyles['HasChanges']"
        >{{ $t("SITE_BUILDER.STATUS.HAS_CHANGES") }}</VcStatus
      >
    </template>

    <template v-if="extended">
      <template v-if="item.startDate || item.endDate">
        <VcStatus
          class="w-auto"
          v-bind="statusStyles['Scheduled']"
          >{{ $t("SITE_BUILDER.STATUS.SCHEDULED") }}</VcStatus
        >
      </template>

      <template v-if="isPersonalized">
        <VcStatus
          class="w-auto"
          v-bind="statusStyles['Access']"
          >{{ $t("SITE_BUILDER.STATUS.PERSONALIZED") }}</VcStatus
        >
      </template>
    </template>
  </div>
</template>

<script lang="ts" setup>
import { computed } from "vue";
import { GroupedSiteBuilderPage } from "../../../api_client/virtocommerce.sitebuilder";
import { PageStatuses } from "../composables";

import { VcStatus } from "@vc-shell/framework/ui";

export interface Props {
  item: GroupedSiteBuilderPage;
  extended?: boolean;
}

const props = defineProps<Props>();

const isPersonalized = computed(() => {
  return !props.item.visibility || props.item.userGroups || props.item.organizationId;
});

const statusStyles: Omit<Record<string, Record<string, unknown>>, "Draft"> = {
  Draft: {
    outline: true,
    variant: "info",
  },
  Archived: {
    outline: true,
    variant: "danger",
  },
  Published: {
    outline: true,
    variant: "success",
  },
  HasChanges: {
    outline: true,
    variant: "warning",
  },
  Scheduled: {
    outline: true,
    variant: "light-danger",
  },
  Access: {
    outline: true,
    variant: "info-dark",
  },
};
</script>
