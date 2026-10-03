if (args is ["--verify-client-manifest", var path])
{
    ClientUpdateDeliverySelfTests.VerifyPublishedManifest(path);
    return 0;
}
return SelfTestSuite.Run();
