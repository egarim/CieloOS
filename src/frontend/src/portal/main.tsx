import React from "react";
import { createRoot } from "react-dom/client";
import Portal from "./Portal";
import "./portal.css";

const rootElement = document.getElementById("root");
if (!rootElement) {
  throw new Error("Portal root element is missing.");
}

createRoot(rootElement).render(
  <React.StrictMode>
    <Portal />
  </React.StrictMode>,
);
