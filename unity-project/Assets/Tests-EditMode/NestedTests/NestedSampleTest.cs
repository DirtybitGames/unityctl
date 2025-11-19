using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NestedTests
{
    public class NestedSampleTest
    {
        [Test]
        public void NestedTestPasses()
        {
            Assert.AreEqual(1, 1);
        }

        [Test]
        public void AnotherNestedTest()
        {
            Assert.IsTrue(true);
        }

        [UnityTest]
        public IEnumerator NestedTestWithEnumeratorPasses()
        {
            yield return null;
            Assert.AreEqual(2, 2);
        }
    }
}
