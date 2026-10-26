// C entry points over Sinsy's label builder.
//
// Sinsy's own synthesis path is a score, a label maker and then an engine. We want the
// middle: the labels are what the Japanese voice was trained on, and everything after them
// (the acoustic model, the bank, the vocoder) is already ours.
//
// The library is static and this wrapper exports only these functions, so Sinsy's bundled
// copy of the HTS engine stays inside and never meets muta_hts' own copy.

#include <cstring>
#include <exception>
#include <string>
#include <vector>

#include "Converter.h"
#include "IReadableStream.h"
#include "LabelMaker.h"
#include "LabelStrings.h"
#include "ScoreDoctor.h"
#include "util_score.h"   // the stream operators: writable << writer
#include "XmlReader.h"

#if defined(_WIN32)
#define MUTA_SINSY_API extern "C" __declspec(dllexport)
#else
#define MUTA_SINSY_API extern "C" __attribute__((visibility("default")))
#endif

namespace
{
bool g_converterFailed = false;

/// One converter for the whole process.
///
/// It owns the language tables, which cost megabytes to load and never change. Building one
/// per call leaked them, and a long project walked into the resulting pressure until the
/// native engine died -- roughly ninety blocks in, at the same point every time.
sinsy::Converter& SharedConverter(const char* dicDir)
{
   static sinsy::Converter* shared = nullptr;
   static sinsy::Converter fallback;

   if (shared == nullptr && !g_converterFailed)
   {
      auto* made = new sinsy::Converter();
      // The flag is a letter per language, lowercase; the tables sit beside it.
      if (made->setLanguages("j", dicDir))
      {
         shared = made;
      }
      else
      {
         g_converterFailed = true;
         delete made;
      }
   }
   return shared != nullptr ? *shared : fallback;
}

/// Copies the joined labels into the caller's buffer, or just reports the size.
int CopyOut(const std::string& text, char* buffer, int bufferSize)
{
   const int needed = static_cast<int>(text.size()) + 1;
   if (buffer == nullptr || bufferSize < needed)
   {
      return needed;
   }
   std::memcpy(buffer, text.data(), text.size());
   buffer[text.size()] = '\0';
   return needed;
}

std::string Join(const sinsy::LabelStrings& labels)
{
   std::string text;
   const char* const* data = labels.getData();
   const size_t count = labels.size();
   for (size_t i = 0; i < count; ++i)
   {
      if (data[i] == nullptr)
      {
         continue;
      }
      text += data[i];
      text += '\n';
   }
   return text;
}
/// The score as text, read from memory: keeps the MusicXML out of the file system.
class MemoryStream : public sinsy::IReadableStream
{
public:
   explicit MemoryStream(const std::string& text) : m_text(text), m_offset(0) {}
   virtual ~MemoryStream() {}

   size_t read(void* buffer, size_t byte) throw (sinsy::StreamException)
   {
      const size_t remaining = m_text.size() - m_offset;
      const size_t take = byte < remaining ? byte : remaining;
      if (take == 0)
      {
         return 0;
      }
      std::memcpy(buffer, m_text.data() + m_offset, take);
      m_offset += take;
      return take;
   }

private:
   const std::string& m_text;
   size_t m_offset;
};

/// Shared tail: repair the score, build the labels, copy them out.
int BuildLabels(sinsy::XmlReader& reader, const char* dicDir, char* buffer, int bufferSize)
{
   sinsy::ScoreDoctor score;
   score << reader;

   sinsy::Converter& converter = SharedConverter(dicDir);
   if (g_converterFailed)
   {
      return -4;
   }

   sinsy::LabelMaker maker(converter);
   maker << score;
   maker.fix();

   sinsy::LabelStrings labels;
   // (monophone, overwriteEnableFlag, timeFlag) as Sinsy's own synthesis passes them.
   maker.outputLabel(labels, false, 1, 2);

   return CopyOut(Join(labels), buffer, bufferSize);
}
}  // namespace

/// Builds the full-context labels for a MusicXML document held in memory.
///
/// Same score as \ref muta_sinsy_labels, without a temporary file: the reader takes any
/// IReadableStream, so a score string is enough.
MUTA_SINSY_API int muta_sinsy_labels_xml(
   const char* xml, const char* dicDir, char* buffer, int bufferSize)
{
   if (xml == nullptr || dicDir == nullptr)
   {
      return -1;
   }

   try
   {
      std::string text(xml);
      if (text.empty())
      {
         return -2;
      }

      MemoryStream stream(text);
      sinsy::XmlReader reader;
      if (!reader.readXml(stream))
      {
         return -3;
      }

      return BuildLabels(reader, dicDir, buffer, bufferSize);
   }
   catch (const std::exception&)
   {
      return -5;
   }
   catch (...)
   {
      return -6;
   }
}
