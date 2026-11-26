<template>
  <VcPopup
    :model-value="true"
    :title="$t('SITE_BUILDER.DESIGNER.HISTORY.DISCARD_TITLE')"
    @close="emit('close')"
  >
    <template #content>
      <div class="pd-discard">
        <p>{{ $t("SITE_BUILDER.DESIGNER.HISTORY.DISCARD_PICK") }}</p>
        <ul class="pd-discard__list">
          <li
            v-for="option in options"
            :key="option.part"
          >
            <label
              class="pd-discard__option"
              :class="{ 'pd-discard__option--off': !option.allowed }"
              :title="option.allowed ? undefined : $t('SITE_BUILDER.DESIGNER.HISTORY.DISCARD_NOT_ALLOWED')"
            >
              <input
                type="checkbox"
                :checked="ticked.includes(option.part)"
                :disabled="!option.allowed"
                @change="toggle(option.part)"
              />
              <span>{{ optionLabel(option) }}</span>
            </label>
          </li>
        </ul>
        <p v-if="options.some((x) => x.part !== 'page')">{{ $t("SITE_BUILDER.DESIGNER.HISTORY.DISCARD_THEME") }}</p>
        <div
          v-for="removal in removals"
          :key="removal.part"
          class="pd-discard__removal"
          role="alert"
        >
          <VcIcon
            icon="lucide-triangle-alert"
            size="s"
            class="pd-discard__removal-icon"
          />
          <div>
            <strong>{{ $t("SITE_BUILDER.DESIGNER.HISTORY.DISCARD_REMOVE_TYPE_TITLE", { name: removal.name }) }}</strong>
            <p>{{ $t("SITE_BUILDER.DESIGNER.HISTORY.DISCARD_REMOVE_TYPE") }}</p>
          </div>
        </div>
        <p v-if="unsaved">{{ $t("SITE_BUILDER.DESIGNER.HISTORY.DISCARD_UNSAVED") }}</p>
      </div>
    </template>

    <template #footer>
      <div class="tw-flex tw-flex-wrap tw-justify-end tw-gap-3">
        <VcButton
          variant="secondary"
          @click="emit('close')"
        >
          {{ $t("SITE_BUILDER.DESIGNER.HISTORY.DISCARD_CANCEL") }}
        </VcButton>
        <VcButton
          :disabled="!unsaved && !choice.page && !choice.theme.length"
          @click="emit('discard', choice)"
        >
          {{ $t(removals.length ? "SITE_BUILDER.DESIGNER.HISTORY.DISCARD_REMOVE_CONFIRM" : "SITE_BUILDER.DESIGNER.HISTORY.DISCARD_CONFIRM") }}
        </VcButton>
      </div>
    </template>
  </VcPopup>
</template>

<script setup lang="ts">
import { computed, ref } from "vue";
import { useI18n } from "vue-i18n";
import { VcButton, VcIcon, VcPopup } from "@vc-shell/framework/ui";
import { discardChoice, removedSectionTypes, type DiscardOption, type DiscardPart, type DiscardThemePart } from "../discard";
import { isCustomSectionPart } from "../customSections";
import { partLabel } from "../history";

const props = defineProps<{
  options: DiscardOption[];
  template: boolean;
  unsaved: boolean;
}>();
const emit = defineEmits<{ discard: [choice: { page: boolean; theme: DiscardThemePart[] }]; close: [] }>();

const { t } = useI18n({ useScope: "global" });
const ticked = ref<DiscardPart[]>(props.options.filter((x) => x.checked).map((x) => x.part));
const toggle = (part: DiscardPart) => (ticked.value = ticked.value.includes(part) ? ticked.value.filter((x) => x !== part) : [...ticked.value, part]);
const choice = computed(() => discardChoice(props.options, ticked.value));
const removals = computed(() => removedSectionTypes(props.options, ticked.value));

function optionLabel(option: DiscardOption) {
  if (!isCustomSectionPart(option.part)) return t(partLabel(option.part, props.template));
  return t(option.removes ? "SITE_BUILDER.DESIGNER.HISTORY.DISCARD_NEW_TYPE" : "SITE_BUILDER.DESIGNER.HISTORY.DISCARD_TYPE", { name: option.name });
}
</script>

<style lang="scss" scoped>
.pd-discard {
  display: flex;
  flex-direction: column;
  gap: 12px;

  p {
    margin: 0;
    color: var(--neutrals-600);
  }

  &__list {
    display: flex;
    flex-direction: column;
    gap: 6px;
    margin: 0;
    padding: 0;
    list-style: none;
  }

  &__option {
    display: flex;
    align-items: center;
    gap: 8px;
    cursor: pointer;

    input {
      accent-color: var(--primary-500);
    }

    &--off {
      color: var(--neutrals-400);
      cursor: not-allowed;
    }
  }

  &__removal {
    display: flex;
    gap: 8px;
    padding: 10px 12px;
    border-radius: 6px;
    background: var(--danger-50);
    color: var(--danger-700);

    p {
      margin: 4px 0 0;
      color: inherit;
    }
  }

  &__removal-icon {
    flex: none;
    margin-top: 2px;
  }
}
</style>
