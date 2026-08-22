import { render, screen } from "@testing-library/react";
import React from "react";
import { expect, test } from "vitest";

function BrandingHeading({ productName }: { productName: string }) {
  return <h1>{productName}</h1>;
}

test("renders product name from branding data", () => {
  render(<BrandingHeading productName="Example OS" />);
  expect(screen.getByRole("heading", { name: "Example OS" })).toBeInTheDocument();
});
