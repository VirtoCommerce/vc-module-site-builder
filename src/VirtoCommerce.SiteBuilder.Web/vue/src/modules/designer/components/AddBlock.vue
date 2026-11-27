<template>
  <VcDropdown
    v-model="open"
    :items="types"
    floating
    close-on-select
    placement="bottom-start"
    @item-click="(x: { type: string }) => designer.addBlock(section.id, x.type)"
  >
    <template #trigger="trigger">
      <slot v-bind="trigger" />
    </template>
    <template #item="{ item }">
      <span class="pd-add-block__item">
        <VcIcon
          :icon="iconOf(item.schema)"
          size="s"
        />
        {{ item.schema.name ?? item.type }}
      </span>
    </template>
  </VcDropdown>
</template>

<script setup lang="ts">
import { computed, ref } from "vue";
import { VcDropdown, VcIcon } from "@vc-shell/framework/ui";
import type { Item } from "../types";
import type { Designer } from "../useDesigner";
import { iconOf } from "../schema";

const props = defineProps<{ designer: Designer; section: Item }>();
defineSlots<{ default(props: { isActive: boolean; toggle: () => void }): unknown }>();

const open = ref(false);
const types = computed(() =>
  (props.designer.sectionSchema(props.section.type)?.blocks ?? [])
    .map((type) => ({ type, schema: props.designer.blockSchema(type) ?? { name: type } }))
    .filter((x) => !x.schema.hidden),
);
</script>

<style lang="scss" scoped>
.pd-add-block__item {
  display: flex;
  align-items: center;
  gap: 8px;
}
</style>
