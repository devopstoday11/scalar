﻿using Moq;
using NUnit.Framework;
using Scalar.Common.NamedPipes;
using Scalar.Service.UI;
using Scalar.UnitTests.Mock.Common;
using System;

namespace Scalar.UnitTests.Windows.ServiceUI
{
    [TestFixture]
    public class ScalarToastRequestHandlerTests
    {
        private NamedPipeMessages.Notification.Request request;
        private ScalarToastRequestHandler toastHandler;
        private Mock<IToastNotifier> mockToastNotifier;
        private MockTracer tracer;

        [SetUp]
        public void Setup()
        {
            this.tracer = new MockTracer();
            this.mockToastNotifier = new Mock<IToastNotifier>(MockBehavior.Strict);
            this.mockToastNotifier.SetupSet(toastNotifier => toastNotifier.UserResponseCallback = It.IsAny<Action<string>>()).Verifiable();
            this.toastHandler = new ScalarToastRequestHandler(this.mockToastNotifier.Object, this.tracer);
            this.request = new NamedPipeMessages.Notification.Request();
        }

        [TestCase]
        public void UpgradeToastIsActionableAndContainsVersionInfo()
        {
            const string version = "1.0.956749.2";

            this.request.Id = NamedPipeMessages.Notification.Request.Identifier.UpgradeAvailable;
            this.request.NewVersion = version;

            this.VerifyToastMessage(
                expectedTitle: "New version " + version + " is available",
                expectedMessage: "click Upgrade button",
                expectedButtonTitle: "Upgrade",
                expectedScalarCmd: "scalar upgrade --confirm");
        }

        [TestCase]
        public void UnknownToastRequestGetsIgnored()
        {
            this.request.Id = (NamedPipeMessages.Notification.Request.Identifier)10;

            this.toastHandler.HandleToastRequest(this.tracer, this.request);

            this.mockToastNotifier.Verify(
                toastNotifier => toastNotifier.Notify(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>()),
                Times.Never());
        }

        private void VerifyToastMessage(
            string expectedTitle,
            string expectedMessage,
            string expectedButtonTitle,
            string expectedScalarCmd)
        {
            this.mockToastNotifier.Setup(toastNotifier => toastNotifier.Notify(
                expectedTitle,
                It.Is<string>(message => message.Contains(expectedMessage)),
                expectedButtonTitle,
                expectedScalarCmd));

            this.toastHandler.HandleToastRequest(this.tracer, this.request);
            this.mockToastNotifier.VerifyAll();
        }
    }
}
