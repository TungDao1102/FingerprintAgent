using System.Collections.Generic;
using FingerprintAgent.Configuration;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FingerprintAgent.Tests.Configuration
{
    public class ConfigMergerTests
    {
        // ---------- Edge cases from D-35 ----------

        [Fact]
        public void Merge_EmptyUser_GetsAllTemplateKeys()
        {
            // Arrange
            var user = JObject.Parse("{}");
            var template = JObject.Parse("{ \"a\": 1, \"b\": 2 }");

            // Act
            var (merged, addedKeys) = ConfigMerger.Merge(user, template);

            // Assert
            Assert.Equal(1, (int)merged["a"]);
            Assert.Equal(2, (int)merged["b"]);
            Assert.Equal(2, addedKeys.Count);
            Assert.Contains("a", addedKeys);
            Assert.Contains("b", addedKeys);
        }

        [Fact]
        public void Merge_UserMissingKey_AddsFromTemplate()
        {
            // Arrange
            var user = JObject.Parse("{ \"a\": 1 }");
            var template = JObject.Parse("{ \"a\": 1, \"b\": 2 }");

            // Act
            var (merged, addedKeys) = ConfigMerger.Merge(user, template);

            // Assert
            Assert.Equal(1, (int)merged["a"]);
            Assert.Equal(2, (int)merged["b"]);
            Assert.Single(addedKeys);
            Assert.Contains("b", addedKeys);
        }

        [Fact]
        public void Merge_UserHasValue_KeepsUserValue()
        {
            // Arrange — D-35: user wins
            var user = JObject.Parse("{ \"a\": 1 }");
            var template = JObject.Parse("{ \"a\": 99 }");

            // Act
            var (merged, addedKeys) = ConfigMerger.Merge(user, template);

            // Assert
            Assert.Equal(1, (int)merged["a"]);
            Assert.Empty(addedKeys);
        }

        [Fact]
        public void Merge_UserMissingTemplateKey_GetsAdded()
        {
            // Arrange
            var user = JObject.Parse("{ \"a\": 1 }");
            var template = JObject.Parse("{ \"a\": 1, \"b\": 2, \"c\": 3 }");

            // Act
            var (merged, addedKeys) = ConfigMerger.Merge(user, template);

            // Assert
            Assert.Equal(1, (int)merged["a"]);
            Assert.Equal(2, (int)merged["b"]);
            Assert.Equal(3, (int)merged["c"]);
            Assert.Equal(2, addedKeys.Count);
            Assert.Contains("b", addedKeys);
            Assert.Contains("c", addedKeys);
        }

        [Fact]
        public void Merge_NestedObject_Recurses()
        {
            // Arrange — user has update section with one key; template adds another key
            var user = JObject.Parse("{ \"update\": { \"enabled\": false } }");
            var template = JObject.Parse("{ \"update\": { \"enabled\": true, \"checkIntervalHours\": 6 } }");

            // Act
            var (merged, addedKeys) = ConfigMerger.Merge(user, template);

            // Assert
            Assert.NotNull(merged["update"]);
            Assert.False((bool)merged["update"]["enabled"]);   // user wins
            Assert.Equal(6, (int)merged["update"]["checkIntervalHours"]);  // added
            Assert.Single(addedKeys);
            Assert.Contains("update.checkIntervalHours", addedKeys);
        }

        [Fact]
        public void Merge_TemplateHasNested_UserDoesNot_AddsWholeSubtree()
        {
            // Arrange — template has new nested section, user doesn't
            var user = JObject.Parse("{}");
            var template = JObject.Parse("{ \"update\": { \"enabled\": false } }");

            // Act
            var (merged, addedKeys) = ConfigMerger.Merge(user, template);

            // Assert
            Assert.NotNull(merged["update"]);
            Assert.False((bool)merged["update"]["enabled"]);
            // Both "update" and "update.enabled" are reported as added
            Assert.Contains("update", addedKeys);
            Assert.Contains("update.enabled", addedKeys);
            Assert.Equal(2, addedKeys.Count);
        }

        [Fact]
        public void Merge_BothEmpty_NoChange()
        {
            // Arrange
            var user = JObject.Parse("{}");
            var template = JObject.Parse("{}");

            // Act
            var (merged, addedKeys) = ConfigMerger.Merge(user, template);

            // Assert
            Assert.Empty(merged.Properties());
            Assert.Empty(addedKeys);
        }

        [Fact]
        public void Merge_PreservesJsonTypes()
        {
            // Arrange — user has int, template has int; verify type preserved (not coerced to string)
            var user = JObject.Parse("{ \"count\": 5 }");
            var template = JObject.Parse("{ \"count\": 10 }");

            // Act
            var (merged, addedKeys) = ConfigMerger.Merge(user, template);

            // Assert
            Assert.Equal(JTokenType.Integer, merged["count"].Type);
            Assert.Equal(5, (int)merged["count"]);
            Assert.Empty(addedKeys);
        }

        [Fact]
        public void Merge_NullUserValue_NotTreatedAsDeleted()
        {
            // Arrange — user explicitly set null; respect that as a user choice
            var user = JObject.Parse("{ \"a\": null }");
            var template = JObject.Parse("{ \"a\": 1, \"b\": 2 }");

            // Act
            var (merged, addedKeys) = ConfigMerger.Merge(user, template);

            // Assert
            Assert.NotNull(merged["a"]); // present (null but present)
            Assert.Equal(JTokenType.Null, merged["a"].Type);
            Assert.Equal(2, (int)merged["b"]);
            Assert.Single(addedKeys);
            Assert.Contains("b", addedKeys);
        }

        [Fact]
        public void Merge_NullTemplateValue_NotAddedToUserConfig()
        {
            // Arrange — WARN-02: template has explicit null for a key user doesn't have.
            // Adding "key": null to user config is useless (template error pattern);
            // the merge should skip null template values entirely.
            var user = JObject.Parse("{ \"a\": 1 }");
            var template = JObject.Parse("{ \"a\": 1, \"b\": null, \"c\": 3 }");

            // Act
            var (merged, addedKeys) = ConfigMerger.Merge(user, template);

            // Assert
            Assert.Equal(1, (int)merged["a"]);
            Assert.False(merged.ContainsKey("b"), "null template values must NOT be added to user config");
            Assert.Equal(3, (int)merged["c"]);
            Assert.Single(addedKeys);
            Assert.Contains("c", addedKeys);
            Assert.DoesNotContain("b", addedKeys);
        }
    }
}
