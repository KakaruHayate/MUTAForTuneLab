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
#include "InputFile.h"
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
}  // namespace

/// Builds the full-context labels for a MusicXML file.
///
/// \param xmlPath   path to the score (UTF-8)
/// \param dicDir     directory holding japanese.*.conf and japanese.*.table
/// \param buffer     destination, or null to ask for the size
/// \param bufferSize bytes available in \p buffer
/// \return the required size in bytes (including the terminator), or a negative error code
MUTA_SINSY_API int muta_sinsy_labels(
   const char* xmlPath, const char* dicDir, char* buffer, int bufferSize)
{
   if (xmlPath == nullptr || dicDir == nullptr)
   {
      return -1;
   }

   try
   {
      sinsy::InputFile xml(xmlPath);
      if (!xml.isValid())
      {
         return -2;
      }

      sinsy::XmlReader reader;
      if (!reader.readXml(xml))
      {
         return -3;
      }

      sinsy::ScoreDoctor score;
      score << reader;

      sinsy::Converter converter;
      // The flag is a letter per language, lowercase; the tables are looked up beside it.
      if (!converter.setLanguages("j", dicDir))
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
   catch (const std::exception&)
   {
      return -5;
   }
   catch (...)
   {
      return -6;
   }
}
