﻿using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using tusdotnet.Models;
using tusdotnet.Stores;
using Xunit;

namespace tusdotnet.test.Tests
{
	public class TusDiskStoreTests : IClassFixture<TusDiskStoreFixture>, IDisposable
	{
		private readonly TusDiskStoreFixture _fixture;

		public TusDiskStoreTests(TusDiskStoreFixture fixture)
		{
			_fixture = fixture;
		}

		[Fact]
		public async Task CreateFileAsync()
		{
			for (var i = 0; i < 10; i++)
			{
				var fileId = await _fixture.Store.CreateFileAsync(i, null, CancellationToken.None);
				var filePath = Path.Combine(_fixture.Path, fileId);
				File.Exists(filePath).ShouldBeTrue();
			}
		}

		[Fact]
		public async Task FileExistsAsync()
		{
			for (var i = 0; i < 10; i++)
			{
				var fileId = await _fixture.Store.CreateFileAsync(i, null, CancellationToken.None);
				var exist = await _fixture.Store.FileExistAsync(fileId, CancellationToken.None);
				exist.ShouldBeTrue();
			}

			for (var i = 0; i < 10; i++)
			{
				var exist = await _fixture.Store.FileExistAsync(Guid.NewGuid().ToString(), CancellationToken.None);
				exist.ShouldBeFalse();
			}

		}

		[Fact]
		public async Task GetUploadLengthAsync()
		{
			var fileId = await _fixture.Store.CreateFileAsync(3000, null, CancellationToken.None);
			var length = await _fixture.Store.GetUploadLengthAsync(fileId, CancellationToken.None);
			length.ShouldBe(3000);

			length = await _fixture.Store.GetUploadLengthAsync(Guid.NewGuid().ToString(), CancellationToken.None);
			length.ShouldBeNull();

			File.Delete(Path.Combine(_fixture.Path, fileId + ".uploadlength"));

			length = await _fixture.Store.GetUploadLengthAsync(fileId, CancellationToken.None);
			length.ShouldBeNull();

			File.Create(Path.Combine(_fixture.Path, fileId + ".uploadlength")).Dispose();

			length = await _fixture.Store.GetUploadLengthAsync(fileId, CancellationToken.None);
			length.ShouldBeNull();

		}

		[Fact]
		public async Task GetUploadOffsetAsync()
		{
			var fileId = await _fixture.Store.CreateFileAsync(100, null, CancellationToken.None);

			var stream = new MemoryStream(new UTF8Encoding(false).GetBytes("Test content"));
			var bytesWritten = await _fixture.Store.AppendDataAsync(fileId, stream, CancellationToken.None);
			bytesWritten.ShouldBe(stream.Length);

			var offset = await _fixture.Store.GetUploadOffsetAsync(fileId, CancellationToken.None);
			offset.ShouldBe(bytesWritten);
		}

		[Fact]
		public async Task AppendDataAsync_Supports_Cancellation()
		{
			var cancellationToken = new CancellationTokenSource();

			// Test cancellation.

			// 30 MB
			const int fileSize = 30 * 1024 * 1024;
			var fileId = await _fixture.Store.CreateFileAsync(fileSize, null, cancellationToken.Token);

			var buffer = new MemoryStream(new byte[fileSize]);

			var appendTask = Task.Run(() => _fixture.Store
				.AppendDataAsync(fileId, buffer, cancellationToken.Token), CancellationToken.None);
			await Task.Delay(10, CancellationToken.None);
			cancellationToken.Cancel();
			long bytesWritten = -1;

			try
			{
				bytesWritten = await appendTask;
				// Should have written something but should not have completed.
				bytesWritten.ShouldBeInRange(1, fileSize - 1);
			}
			catch (TaskCanceledException)
			{
				// The Owin test server throws this exception instead of just disconnecting the client.
				// If this happens just ignore the error and verify that the file has been written properly below.
			}

			var fileOffset = await _fixture.Store.GetUploadOffsetAsync(fileId, CancellationToken.None);
			if (bytesWritten != -1)
			{
				fileOffset.ShouldBe(bytesWritten);
			}
			else
			{
				fileOffset.ShouldBeInRange(1, fileSize - 1);
			}

			var fileOnDiskSize = new FileInfo(Path.Combine(_fixture.Path, fileId)).Length;
			fileOnDiskSize.ShouldBe(fileOffset);
		}

		[Fact]
		public async Task AppendDataAsync_Throws_Exception_If_More_Data_Than_Upload_Length_Is_Provided()
		{
			// Test that it does not allow more than upload length to be written.

			var fileId = await _fixture.Store.CreateFileAsync(100, null, CancellationToken.None);

			var storeException = await Should.ThrowAsync<TusStoreException>(
				async () => await _fixture.Store.AppendDataAsync(fileId, new MemoryStream(new byte[101]), CancellationToken.None));

			storeException.Message.ShouldBe("Stream contains more data than the file's upload length. Stream data: 101, upload length: 100.");
		}

		[Fact]
		public async Task AppendDataAsync_Returns_Zero_If_File_Is_Already_Complete()
		{
			var fileId = await _fixture.Store.CreateFileAsync(100, null, CancellationToken.None);
			var length = await _fixture.Store.AppendDataAsync(fileId, new MemoryStream(new byte[100]), CancellationToken.None);
			length.ShouldBe(100);

			length = await _fixture.Store.AppendDataAsync(fileId, new MemoryStream(new byte[1]), CancellationToken.None);
			length.ShouldBe(0);
		}

		[Fact]
		public async Task GetFileAsync_Returns_File_If_The_File_Exist()
		{
			var fileId = await _fixture.Store.CreateFileAsync(100, null, CancellationToken.None);

			var content = Enumerable.Range(0, 100).Select(f => (byte)f).ToArray();

			await _fixture.Store.AppendDataAsync(fileId, new MemoryStream(content), CancellationToken.None);

			var file = await _fixture.Store.GetFileAsync(fileId, CancellationToken.None);

			file.Id.ShouldBe(fileId);

			using (var fileContent = await file.GetContentAsync(CancellationToken.None))
			{
				fileContent.Length.ShouldBe(content.Length);

				var fileContentBuffer = new byte[fileContent.Length];
				fileContent.Read(fileContentBuffer, 0, fileContentBuffer.Length);

				for (var i = 0; i < content.Length; i++)
				{
					fileContentBuffer[i].ShouldBe(content[i]);
				}
			}
		}

		[Fact]
		public async Task GetFileAsync_Returns_Null_If_The_File_Does_Not_Exist()
		{
			var file = await _fixture.Store.GetFileAsync(Guid.NewGuid().ToString(), CancellationToken.None);
			file.ShouldBeNull();
		}

		[Fact]
		public async Task CreateFileAsync_Creates_Metadata_Properly()
		{
			var fileId = await _fixture.Store.CreateFileAsync(1, "key wrbDgMSaxafMsw==", CancellationToken.None);
			fileId.ShouldNotBeNull();

			var file = await _fixture.Store.GetFileAsync(fileId, CancellationToken.None);
			var metadata = await file.GetMetadataAsync(CancellationToken.None);
			metadata.ContainsKey("key").ShouldBeTrue();
			// Correct encoding
			metadata["key"].GetString(new UTF8Encoding()).ShouldBe("¶ÀĚŧ̳");
			// Wrong encoding just to test that the result is different.
			metadata["key"].GetString(new UTF7Encoding()).ShouldBe("Â¶ÃÄÅ§Ì³");
			metadata["key"].GetBytes().ShouldBe(new byte[] { 194, 182, 195, 128, 196, 154, 197, 167, 204, 179 });
		}

		[Fact]
		public async Task GetUploadMetadataAsync()
		{
			const string metadataConst = "key wrbDgMSaxafMsw==";
			var fileId = await _fixture.Store.CreateFileAsync(1, metadataConst, CancellationToken.None);

			var metadata = await _fixture.Store.GetUploadMetadataAsync(fileId, CancellationToken.None);
			metadata.ShouldBe(metadataConst);

			fileId = await _fixture.Store.CreateFileAsync(1, null, CancellationToken.None);
			metadata = await _fixture.Store.GetUploadMetadataAsync(fileId, CancellationToken.None);
			metadata.ShouldBeNull();

			fileId = await _fixture.Store.CreateFileAsync(1, "", CancellationToken.None);
			metadata = await _fixture.Store.GetUploadMetadataAsync(fileId, CancellationToken.None);
			metadata.ShouldBeNull();
		}

		[Fact]
		public async Task DeleteFileAsync()
		{
			const string metadataConst = "key wrbDgMSaxafMsw==";
			for (var i = 0; i < 10; i++)
			{
				var fileId = await _fixture.Store.CreateFileAsync(i + 1, i % 2 == 0 ? null : metadataConst, CancellationToken.None);
				var exist = await _fixture.Store.FileExistAsync(fileId, CancellationToken.None);
				exist.ShouldBeTrue();

				// Verify that all files exist.
				var filePath = Path.Combine(_fixture.Path, fileId);
				var uploadLengthPath = $"{filePath}.uploadlength";
				var metaPath = $"{filePath}.metadata";
				File.Exists(filePath).ShouldBeTrue();
				File.Exists(uploadLengthPath).ShouldBeTrue();
				File.Exists(metaPath).ShouldBeTrue();

				await _fixture.Store.DeleteFileAsync(fileId, CancellationToken.None);

				// Verify that all files were deleted.
				File.Exists(filePath).ShouldBeFalse();
				File.Exists(uploadLengthPath).ShouldBeFalse();
				File.Exists(metaPath).ShouldBeFalse();
			}
		}

		[Fact]
		public async Task GetSupportedAlgorithmsAsync()
		{
			var algorithms = await _fixture.Store.GetSupportedAlgorithmsAsync(CancellationToken.None);
			// ReSharper disable PossibleMultipleEnumeration
			algorithms.ShouldNotBeNull();
			algorithms.Count().ShouldBe(1);
			algorithms.First().ShouldBe("sha1");
			// ReSharper restore PossibleMultipleEnumeration
		}

		[Fact]
		public async Task VerifyChecksumAsync()
		{
			const string checksum = "9jSJuBxGMnq4UffwNYM8ct1tYQQ=";
			const string message = "Hello World 12345!!@@åäö";
			var buffer = new UTF8Encoding(false).GetBytes(message);

			var fileId = await _fixture.Store.CreateFileAsync(buffer.Length, null, CancellationToken.None);
			using (var stream = new MemoryStream(buffer))
			{
				await _fixture.Store.AppendDataAsync(fileId, stream, CancellationToken.None);
			}

			var checksumOk = await _fixture.Store.VerifyChecksumAsync(fileId, "sha1", Convert.FromBase64String(checksum),
				CancellationToken.None);

			checksumOk.ShouldBeTrue();
		}

		[Fact]
		public async Task VerifyChecksumAsync_Data_Is_Truncated_If_Verification_Fails()
		{
			// Checksum is for "hello world"
			const string incorrectChecksum = "Kq5sNclPz7QV2+lfQIuc6R7oRu0=";
			const string message = "Hello World 12345!!@@åäö";

			var buffer = new UTF8Encoding(false).GetBytes(message);

			// Test complete upload
			var fileId = await _fixture.Store.CreateFileAsync(buffer.Length, null, CancellationToken.None);
			using (var stream = new MemoryStream(buffer))
			{
				await _fixture.Store.AppendDataAsync(fileId, stream, CancellationToken.None);
			}

			var checksumOk = await _fixture.Store
				.VerifyChecksumAsync(fileId, "sha1", Convert.FromBase64String(incorrectChecksum), CancellationToken.None);

			// File should not have been saved.
			checksumOk.ShouldBeFalse();
			var filePath = Path.Combine(_fixture.Path, fileId);
			new FileInfo(filePath).Length.ShouldBe(0);

			// Test chunked upload
			fileId = await _fixture.Store.CreateFileAsync(buffer.Length, null, CancellationToken.None);
			using (var stream = new MemoryStream(buffer.Take(10).ToArray()))
			{
				// Write first 10 bytes
				await _fixture.Store.AppendDataAsync(fileId, stream, CancellationToken.None);
			}

			using (var stream = new MemoryStream(buffer.Skip(10).ToArray()))
			{
				// Skip first 10 bytes and write the rest
				await _fixture.Store.AppendDataAsync(fileId, stream, CancellationToken.None);
			}

			checksumOk = await _fixture.Store.VerifyChecksumAsync(fileId, "sha1", Convert.FromBase64String(incorrectChecksum),
				CancellationToken.None);

			// Only first chunk should have been saved.
			checksumOk.ShouldBeFalse();
			filePath = Path.Combine(_fixture.Path, fileId);
			new FileInfo(filePath).Length.ShouldBe(10);
		}

		public void Dispose()
		{
			_fixture.ClearPath();
		}
	}

	public class TusDiskStoreFixture : IDisposable
	{
		public string Path { get; set; }
		public TusDiskStore Store { get; set; }

		public TusDiskStoreFixture()
		{
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetTempFileName().Replace(".", ""));
			ClearPath();

			Store = new TusDiskStore(Path);
		}

		public void Dispose()
		{
			Directory.Delete(Path, true);
		}

		public void ClearPath()
		{
			if (Directory.Exists(Path))
			{
				Directory.Delete(Path, true);
			}
			Directory.CreateDirectory(Path);
		}
	}
}
