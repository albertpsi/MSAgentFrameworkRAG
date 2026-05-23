import "./globals.css";

export const metadata = {
  title: "Modern Banking & Insurance RAG AI Assistant | Antigravity",
  description: "Advanced contextual support agent utilizing Microsoft Agents AI Framework and Pinecone vector store search.",
};

export default function RootLayout({ children }) {
  return (
    <html lang="en" className="dark-theme">
      <head>
        {/* Modern Premium Fonts */}
        <link rel="preconnect" href="https://fonts.googleapis.com" />
        <link rel="preconnect" href="https://fonts.gstatic.com" crossOrigin="anonymous" />
        <link href="https://fonts.googleapis.com/css2?family=Outfit:wght@300;400;500;600;700&family=Plus+Jakarta+Sans:wght@300;400;500;600;700&display=swap" rel="stylesheet" />
        {/* FontAwesome Icons */}
        <link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.4.0/css/all.min.css" />
      </head>
      <body>
        {children}
      </body>
    </html>
  );
}
