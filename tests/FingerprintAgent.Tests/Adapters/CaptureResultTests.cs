using System;
using System.Text.RegularExpressions;
using FingerprintAgent.Adapters;
using Xunit;

namespace FingerprintAgent.Tests.Adapters
{
    public class CaptureResultTests
    {
        [Fact]
        public void Ok_SetsIsSuccessTrue()
        {
            // Arrange & Act
            CaptureResult result = CaptureResult.Ok(new byte[] { 1, 2, 3 });

            // Assert
            Assert.True(result.IsSuccess);
        }

        [Fact]
        public void Ok_SetsProvidedImageBytes()
        {
            // Arrange
            byte[] imageBytes = new byte[] { 0x10, 0x20, 0x30, 0x40 };

            // Act
            CaptureResult result = CaptureResult.Ok(imageBytes);

            // Assert
            Assert.Same(imageBytes, result.ImageBytes);
        }

        [Fact]
        public void Ok_DefaultsMimeTypeToImagePng()
        {
            // Arrange & Act
            CaptureResult result = CaptureResult.Ok(new byte[] { 1 });

            // Assert
            Assert.Equal("image/png", result.MimeType);
        }

        [Fact]
        public void Ok_SetsCapturedAtToCurrentUtcIso8601()
        {
            // Arrange
            DateTime before = DateTime.UtcNow.AddSeconds(-1);
            string iso8601Pattern = @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}";

            // Act
            CaptureResult result = CaptureResult.Ok(new byte[] { 1 });
            DateTime after = DateTime.UtcNow.AddSeconds(1);

            // Assert
            Assert.NotNull(result.CapturedAt);
            Assert.Matches(iso8601Pattern, result.CapturedAt);
            DateTime parsed = DateTime.Parse(result.CapturedAt, null, System.Globalization.DateTimeStyles.RoundtripKind);
            Assert.InRange(parsed, before, after);
        }

        [Fact]
        public void Ok_SetsProvidedDeviceId()
        {
            // Arrange
            string deviceId = "scanner-42";

            // Act
            CaptureResult result = CaptureResult.Ok(new byte[] { 1 }, deviceId: deviceId);

            // Assert
            Assert.Equal(deviceId, result.DeviceId);
        }

        [Fact]
        public void Ok_LeavesErrorMessageAndErrorCodeNull()
        {
            // Arrange & Act
            CaptureResult result = CaptureResult.Ok(new byte[] { 1 });

            // Assert
            Assert.Null(result.ErrorMessage);
            Assert.Null(result.ErrorCode);
        }

        [Fact]
        public void Ok_DefaultsWidthAndHeightToZero()
        {
            // Arrange & Act
            CaptureResult result = CaptureResult.Ok(new byte[] { 1 });

            // Assert
            Assert.Equal(0, result.Width);
            Assert.Equal(0, result.Height);
        }

        [Fact]
        public void Fail_SetsIsSuccessFalse()
        {
            // Arrange & Act
            CaptureResult result = CaptureResult.Fail("SCANNER_NOT_CONNECTED", "msg");

            // Assert
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Fail_SetsProvidedErrorCode()
        {
            // Arrange & Act
            CaptureResult result = CaptureResult.Fail("CAPTURE_TIMEOUT", "msg");

            // Assert
            Assert.Equal("CAPTURE_TIMEOUT", result.ErrorCode);
        }

        [Fact]
        public void Fail_SetsProvidedErrorMessage()
        {
            // Arrange & Act
            CaptureResult result = CaptureResult.Fail("CODE", "Something broke");

            // Assert
            Assert.Equal("Something broke", result.ErrorMessage);
        }

        [Fact]
        public void Fail_SetsCapturedAtToCurrentUtcIso8601()
        {
            // Arrange
            DateTime before = DateTime.UtcNow.AddSeconds(-1);
            string iso8601Pattern = @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}";

            // Act
            CaptureResult result = CaptureResult.Fail("CODE", "msg");
            DateTime after = DateTime.UtcNow.AddSeconds(1);

            // Assert
            Assert.NotNull(result.CapturedAt);
            Assert.Matches(iso8601Pattern, result.CapturedAt);
            DateTime parsed = DateTime.Parse(result.CapturedAt, null, System.Globalization.DateTimeStyles.RoundtripKind);
            Assert.InRange(parsed, before, after);
        }

        [Fact]
        public void Fail_LeavesImageBytesAndDeviceIdNull()
        {
            // Arrange & Act
            CaptureResult result = CaptureResult.Fail("CODE", "msg");

            // Assert
            Assert.Null(result.ImageBytes);
            Assert.Null(result.DeviceId);
        }
    }
}
