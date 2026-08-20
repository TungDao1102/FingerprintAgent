using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace FingerprintAgent.Configuration
{
    /// <summary>
    /// Additive merge of template keys into userConfig (D-35).
    ///
    /// - Keys present in template but absent from userConfig: added with template value.
    /// - Keys present in both: user value is preserved (template is NEVER applied to existing user keys).
    /// - User deletions are respected: keys absent from userConfig stay absent.
    /// - Nested JObject values: recurse.
    /// - Explicit user null: respected (not treated as deletion).
    ///
    /// Does NOT use Newtonsoft.Json.Linq.JObject.Merge() — its default is REPLACE,
    /// the opposite of additive merge semantics required here.
    /// </summary>
    public static class ConfigMerger
    {
        /// <summary>
        /// Convenience wrapper around <see cref="MergeCore"/> that discards the skipped-null-key list.
        /// Prefer MergeCore when the caller wants to surface template bugs (null defaults) via logs.
        /// Returns the merged userConfig (mutated in place AND returned for chaining) plus the
        /// list of keys that were added. Added keys are reported with full dotted-path
        /// (e.g. "update.checkIntervalHours") so logs are unambiguous.
        /// </summary>
        public static (JObject merged, IReadOnlyList<string> addedKeys) Merge(JObject userConfig, JObject template)
        {
            var (merged, addedKeys, _) = MergeCore(userConfig, template);
            return (merged, addedKeys);
        }

        /// <summary>
        /// Returns the merged userConfig (mutated in place AND returned for chaining) plus two
        /// lists: keys added from template into userConfig (dotted-path), and template keys that
        /// were explicitly null and therefore skipped (WARN-02). Skipped-null tracking is
        /// silent at the JSON level but surfaces here so ConfigLoader can log a warning —
        /// a template shipping "key": null is always a bug worth flagging.
        /// </summary>
        public static (JObject merged, IReadOnlyList<string> addedKeys, IReadOnlyList<string> skippedNullKeys) MergeCore(
            JObject userConfig, JObject template)
        {
            var added = new List<string>();
            var skippedNullKeys = new List<string>();
            MergeInto(userConfig, template, prefix: "", added, skippedNullKeys);
            return (userConfig, added, skippedNullKeys);
        }

        private static void MergeInto(JObject userObj, JObject templateObj, string prefix, List<string> added, List<string> skippedNullKeys)
        {
            foreach (var templateProp in templateObj.Properties())
            {
                var key = templateProp.Name;
                var fullKey = string.IsNullOrEmpty(prefix) ? key : prefix + "." + key;
                var templateValue = templateProp.Value;

                // WARN-02: skip explicit null template values. Adding "key": null to user
                // config is never useful — it's a template error to ship null defaults.
                // Null on the user side (WARN-01 documented case) is still respected.
                if (templateValue.Type == JTokenType.Null)
                {
                    skippedNullKeys.Add(fullKey);
                    continue;
                }

                if (!userObj.ContainsKey(key))
                {
                    // Key missing in user config → add from template (DeepClone to detach reference)
                    userObj[key] = templateValue.DeepClone();
                    added.Add(fullKey);

                    // Also report each leaf in added subtrees so merge.log shows
                    // fine-grained additions (per RESEARCH.md §6 format).
                    if (templateValue is JObject addedSubtree)
                    {
                        foreach (var leafProp in addedSubtree.Properties())
                        {
                            added.Add(fullKey + "." + leafProp.Name);
                        }
                    }
                }
                else
                {
                    var userValue = userObj[key];
                    if (userValue is JObject userChild
                        && templateValue is JObject templateChild)
                    {
                        // Both objects → recurse to merge nested keys
                        MergeInto(userChild, templateChild, fullKey, added, skippedNullKeys);
                    }
                    else if (userValue is JArray userArray
                        && templateValue is JArray templateArray)
                    {
                        // WARN-01: arrays merge element-wise. Preserve user order, append
                        // any template-only elements to the end. Without this, template
                        // upgrades that add a new scanner vendor (e.g. adding "Futronic")
                        // silently lose the addition because the user config keeps its
                        // older array verbatim.
                        foreach (var templateElem in templateArray)
                        {
                            bool found = false;
                            foreach (var userElem in userArray)
                            {
                                if (JToken.DeepEquals(userElem, templateElem))
                                {
                                    found = true;
                                    break;
                                }
                            }
                            if (!found)
                            {
                                userArray.Add(templateElem.DeepClone());
                                // Per-element report for merge.log granularity — operators
                                // see exactly which vendors were appended, not just "priority".
                                added.Add($"{fullKey}[{templateElem}]");
                            }
                        }
                    }
                    // Else: user has a value of a different type, or a scalar that conflicts
                    // with template → preserve user's choice (D-35).
                }
            }
        }
    }
}
